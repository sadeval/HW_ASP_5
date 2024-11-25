namespace HW_ASP_5.Models
{
    public class UserService
    {
        public int UserId { get; set; } // Внешний ключ на User
        public User? User { get; set; } // Навигационное свойство

        public int ServiceId { get; set; } // Внешний ключ на Service
        public Service? Service { get; set; } // Навигационное свойство

        // Дополнительная информация, специфичная для регистрации пользователя на услугу
        public string? AdditionalInfo { get; set; }
    }
}
