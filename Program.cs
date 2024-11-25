using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using System.Text.Json;
using System;
using HW_ASP_5.Models;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Extensions.Logging;

namespace HW_ASP_5
{
    public class Program
    {
        public static async Task Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            builder.Logging.ClearProviders();
            builder.Logging.AddConsole(); 
            builder.Logging.AddDebug();  

            builder.Services.AddDbContext<AppDbContext>(options =>
                options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

            builder.Services.AddMemoryCache();

            builder.Services.AddEndpointsApiExplorer();
            builder.Services.AddSwaggerGen();

            var app = builder.Build();

            if (app.Environment.IsDevelopment())
            {
                app.UseSwagger();
                app.UseSwaggerUI();
            }

            // Загрузка предопределённых услуг из JSON
            using (var scope = app.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

                if (!db.Services.Any())
                {
                    logger.LogInformation("Загрузка предопределённых услуг из JSON файла.");
                    var servicesJson = await File.ReadAllTextAsync("services.json");
                    var services = JsonSerializer.Deserialize<List<Service>>(servicesJson);

                    if (services != null)
                    {
                        db.Services.AddRange(services);
                        await db.SaveChangesAsync();
                        logger.LogInformation("Услуги успешно загружены в базу данных.");
                    }
                    else
                    {
                        logger.LogWarning("Не удалось десериализовать услуги из JSON файла.");
                    }
                }
                else
                {
                    logger.LogInformation("Услуги уже существуют в базе данных. Пропуск загрузки из JSON.");
                }
            }

            // Регистрация пользователя на услуги
            app.MapPost("/register", async (RegistrationRequest request, AppDbContext db, ILogger<Program> logger) =>
            {
                // Простая валидация
                if (string.IsNullOrWhiteSpace(request.UserName) ||
                    string.IsNullOrWhiteSpace(request.Email) ||
                    string.IsNullOrWhiteSpace(request.PhoneNumber))
                {
                    logger.LogWarning("Bad Request: отсутствуют обязательные поля.");
                    return Results.BadRequest("Имя пользователя, Email и номер телефона обязательны.");
                }

                using var transaction = await db.Database.BeginTransactionAsync();
                logger.LogInformation("Начата транзакция для регистрации пользователя: {UserName}", request.UserName);

                try
                {
                    var user = new User
                    {
                        UserName = request.UserName,
                        Email = request.Email,
                        PhoneNumber = request.PhoneNumber
                    };

                    db.Users.Add(user);
                    await db.SaveChangesAsync();
                    logger.LogInformation("Пользователь {UserName} добавлен с ID {UserId}.", user.UserName, user.Id);

                    var services = await db.Services
                        .Where(s => request.ServiceIds.Contains(s.Id))
                        .ToListAsync();

                    foreach (var service in services)
                    {
                        var userService = new UserService
                        {
                            UserId = user.Id,
                            ServiceId = service.Id,
                            AdditionalInfo = request.AdditionalInfo != null && request.AdditionalInfo.ContainsKey(service.Id)
                                ? request.AdditionalInfo[service.Id]
                                : string.Empty
                        };
                        db.UserServices.Add(userService);
                        logger.LogInformation("Пользователь {UserName} зарегистрирован на услугу {ServiceName}.", user.UserName, service.Name);
                    }

                    await db.SaveChangesAsync();
                    await transaction.CommitAsync();
                    logger.LogInformation("Транзакция успешно завершена для пользователя {UserName}.", user.UserName);

                    return Results.Created($"/users/{user.Id}/services", user);
                }
                catch (Exception ex)
                {
                    await transaction.RollbackAsync();
                    logger.LogError(ex, "Ошибка при регистрации пользователя {UserName}. Транзакция откатилась.", request.UserName);
                    return Results.StatusCode(500);
                }
            })
            .WithName("RegisterUser")
            .Accepts<RegistrationRequest>("application/json")
            .Produces<User>(201)
            .Produces(400)
            .Produces(500);

            // Просмотр зарегистрированных услуг пользователя
            app.MapGet("/users/{userId}/services", async (int userId, AppDbContext db, ILogger<Program> logger) =>
            {
                logger.LogInformation("Получение услуг для пользователя с ID {UserId}.", userId);
                var user = await db.Users.FindAsync(userId);
                if (user == null)
                {
                    logger.LogWarning("Пользователь с ID {UserId} не найден.", userId);
                    return Results.NotFound($"Пользователь с ID {userId} не найден.");
                }

                var services = await db.UserServices
                    .Where(us => us.UserId == userId)
                    .Include(us => us.Service)
                    .Select(us => new UserServiceResponse
                    {
                        ServiceId = us.Service.Id,
                        ServiceName = us.Service.Name,
                        Description = us.Service.Description,
                        AdditionalInfo = us.AdditionalInfo
                    })
                    .ToListAsync();

                logger.LogInformation("Пользователь {UserName} имеет {ServiceCount} зарегистрированных услуг.", user.UserName, services.Count);

                return Results.Ok(services);
            })
            .WithName("GetUserServices")
            .Produces<List<UserServiceResponse>>(200)
            .Produces(404);

            // Редактирование регистрационных данных пользователя
            app.MapPut("/users/{userId}", async (int userId, UpdateRegistrationRequest request, AppDbContext db, ILogger<Program> logger) =>
            {
                logger.LogInformation("Редактирование данных пользователя с ID {UserId}.", userId);
                var user = await db.Users
                    .Include(u => u.UserServices)
                    .FirstOrDefaultAsync(u => u.Id == userId);

                if (user == null)
                {
                    logger.LogWarning("Пользователь с ID {UserId} не найден для редактирования.", userId);
                    return Results.NotFound($"Пользователь с ID {userId} не найден.");
                }

                // Обновление данных пользователя
                user.UserName = request.UserName;
                user.Email = request.Email;
                user.PhoneNumber = request.PhoneNumber;
                logger.LogInformation("Данные пользователя с ID {UserId} обновлены.", userId);

                // Обновление списка услуг, если предоставлен
                if (request.ServiceIds != null && request.ServiceIds.Any())
                {
                    logger.LogInformation("Обновление услуг для пользователя с ID {UserId}.", userId);
                    // Удаление существующих связей
                    db.UserServices.RemoveRange(user.UserServices);
                    logger.LogInformation("Старые связи услуг удалены для пользователя с ID {UserId}.", userId);

                    // Добавление новых связей
                    var services = await db.Services
                        .Where(s => request.ServiceIds.Contains(s.Id))
                        .ToListAsync();

                    foreach (var service in services)
                    {
                        var userService = new UserService
                        {
                            UserId = user.Id,
                            ServiceId = service.Id,
                            AdditionalInfo = request.AdditionalInfo != null && request.AdditionalInfo.ContainsKey(service.Id)
                                ? request.AdditionalInfo[service.Id]
                                : string.Empty
                        };
                        db.UserServices.Add(userService);
                        logger.LogInformation("Пользователь {UserName} зарегистрирован на услугу {ServiceName}.", user.UserName, service.Name);
                    }
                }

                await db.SaveChangesAsync();
                logger.LogInformation("Изменения сохранены для пользователя с ID {UserId}.", userId);

                return Results.NoContent();
            })
            .WithName("UpdateUser")
            .Accepts<UpdateRegistrationRequest>("application/json")
            .Produces(204)
            .Produces(404);

            // Просмотр Списка Доступных Услуг (с кэшированием)
            _ = app.MapGet("/services", async (AppDbContext db, IMemoryCache cache, ILogger<Program> logger) =>
            {
                logger.LogInformation("Получение списка доступных услуг.");
                if (!cache.TryGetValue("services", out List<Service> services))
                {
                    logger.LogInformation("Услуги не найдены в кэше. Загрузка из базы данных.");
                    services = await db.Services.ToListAsync();

                    var cacheOptions = new MemoryCacheEntryOptions()
                        .SetSlidingExpiration(TimeSpan.FromMinutes(30));

                    cache.Set("services", services, cacheOptions);
                    logger.LogInformation("Услуги закешированы на 30 минут.");
                }
                else
                {
                    logger.LogInformation("Услуги получены из кэша.");
                }

                return Results.Ok(services);
            })
            .WithName("GetServices")
            .Produces<List<Service>>(200);

            app.UseExceptionHandler(errorApp =>
            {
                errorApp.Run(async context =>
                {
                    context.Response.StatusCode = 500;
                    context.Response.ContentType = "application/json";

                    var errorFeature = context.Features.Get<IExceptionHandlerFeature>();
                    if (errorFeature != null)
                    {
                        var ex = errorFeature.Error;
                        var err = new { message = ex.Message };

                        var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
                        logger.LogError(ex, "Необработанное исключение.");

                        await context.Response.WriteAsJsonAsync(err);
                    }
                });
            });

            app.Run();
        }
    }
}
