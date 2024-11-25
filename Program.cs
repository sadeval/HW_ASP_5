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

            // �������� ��������������� ����� �� JSON
            using (var scope = app.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

                if (!db.Services.Any())
                {
                    logger.LogInformation("�������� ��������������� ����� �� JSON �����.");
                    var servicesJson = await File.ReadAllTextAsync("services.json");
                    var services = JsonSerializer.Deserialize<List<Service>>(servicesJson);

                    if (services != null)
                    {
                        db.Services.AddRange(services);
                        await db.SaveChangesAsync();
                        logger.LogInformation("������ ������� ��������� � ���� ������.");
                    }
                    else
                    {
                        logger.LogWarning("�� ������� ��������������� ������ �� JSON �����.");
                    }
                }
                else
                {
                    logger.LogInformation("������ ��� ���������� � ���� ������. ������� �������� �� JSON.");
                }
            }

            // ����������� ������������ �� ������
            app.MapPost("/register", async (RegistrationRequest request, AppDbContext db, ILogger<Program> logger) =>
            {
                // ������� ���������
                if (string.IsNullOrWhiteSpace(request.UserName) ||
                    string.IsNullOrWhiteSpace(request.Email) ||
                    string.IsNullOrWhiteSpace(request.PhoneNumber))
                {
                    logger.LogWarning("Bad Request: ����������� ������������ ����.");
                    return Results.BadRequest("��� ������������, Email � ����� �������� �����������.");
                }

                using var transaction = await db.Database.BeginTransactionAsync();
                logger.LogInformation("������ ���������� ��� ����������� ������������: {UserName}", request.UserName);

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
                    logger.LogInformation("������������ {UserName} �������� � ID {UserId}.", user.UserName, user.Id);

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
                        logger.LogInformation("������������ {UserName} ��������������� �� ������ {ServiceName}.", user.UserName, service.Name);
                    }

                    await db.SaveChangesAsync();
                    await transaction.CommitAsync();
                    logger.LogInformation("���������� ������� ��������� ��� ������������ {UserName}.", user.UserName);

                    return Results.Created($"/users/{user.Id}/services", user);
                }
                catch (Exception ex)
                {
                    await transaction.RollbackAsync();
                    logger.LogError(ex, "������ ��� ����������� ������������ {UserName}. ���������� ����������.", request.UserName);
                    return Results.StatusCode(500);
                }
            })
            .WithName("RegisterUser")
            .Accepts<RegistrationRequest>("application/json")
            .Produces<User>(201)
            .Produces(400)
            .Produces(500);

            // �������� ������������������ ����� ������������
            app.MapGet("/users/{userId}/services", async (int userId, AppDbContext db, ILogger<Program> logger) =>
            {
                logger.LogInformation("��������� ����� ��� ������������ � ID {UserId}.", userId);
                var user = await db.Users.FindAsync(userId);
                if (user == null)
                {
                    logger.LogWarning("������������ � ID {UserId} �� ������.", userId);
                    return Results.NotFound($"������������ � ID {userId} �� ������.");
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

                logger.LogInformation("������������ {UserName} ����� {ServiceCount} ������������������ �����.", user.UserName, services.Count);

                return Results.Ok(services);
            })
            .WithName("GetUserServices")
            .Produces<List<UserServiceResponse>>(200)
            .Produces(404);

            // �������������� ��������������� ������ ������������
            app.MapPut("/users/{userId}", async (int userId, UpdateRegistrationRequest request, AppDbContext db, ILogger<Program> logger) =>
            {
                logger.LogInformation("�������������� ������ ������������ � ID {UserId}.", userId);
                var user = await db.Users
                    .Include(u => u.UserServices)
                    .FirstOrDefaultAsync(u => u.Id == userId);

                if (user == null)
                {
                    logger.LogWarning("������������ � ID {UserId} �� ������ ��� ��������������.", userId);
                    return Results.NotFound($"������������ � ID {userId} �� ������.");
                }

                // ���������� ������ ������������
                user.UserName = request.UserName;
                user.Email = request.Email;
                user.PhoneNumber = request.PhoneNumber;
                logger.LogInformation("������ ������������ � ID {UserId} ���������.", userId);

                // ���������� ������ �����, ���� ������������
                if (request.ServiceIds != null && request.ServiceIds.Any())
                {
                    logger.LogInformation("���������� ����� ��� ������������ � ID {UserId}.", userId);
                    // �������� ������������ ������
                    db.UserServices.RemoveRange(user.UserServices);
                    logger.LogInformation("������ ����� ����� ������� ��� ������������ � ID {UserId}.", userId);

                    // ���������� ����� ������
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
                        logger.LogInformation("������������ {UserName} ��������������� �� ������ {ServiceName}.", user.UserName, service.Name);
                    }
                }

                await db.SaveChangesAsync();
                logger.LogInformation("��������� ��������� ��� ������������ � ID {UserId}.", userId);

                return Results.NoContent();
            })
            .WithName("UpdateUser")
            .Accepts<UpdateRegistrationRequest>("application/json")
            .Produces(204)
            .Produces(404);

            // �������� ������ ��������� ����� (� ������������)
            _ = app.MapGet("/services", async (AppDbContext db, IMemoryCache cache, ILogger<Program> logger) =>
            {
                logger.LogInformation("��������� ������ ��������� �����.");
                if (!cache.TryGetValue("services", out List<Service> services))
                {
                    logger.LogInformation("������ �� ������� � ����. �������� �� ���� ������.");
                    services = await db.Services.ToListAsync();

                    var cacheOptions = new MemoryCacheEntryOptions()
                        .SetSlidingExpiration(TimeSpan.FromMinutes(30));

                    cache.Set("services", services, cacheOptions);
                    logger.LogInformation("������ ������������ �� 30 �����.");
                }
                else
                {
                    logger.LogInformation("������ �������� �� ����.");
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
                        logger.LogError(ex, "�������������� ����������.");

                        await context.Response.WriteAsJsonAsync(err);
                    }
                });
            });

            app.Run();
        }
    }
}
