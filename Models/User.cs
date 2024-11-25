namespace HW_ASP_5.Models
{
    public class User
    {
        public int Id { get; set; } // Первичный ключ
        public string? UserName { get; set; } 
        public string? Email { get; set; } 
        public string? PhoneNumber { get; set; } 

        // Навигационное свойство
        public ICollection<UserService> UserServices { get; set; }
    }
}
