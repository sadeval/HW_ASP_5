namespace HW_ASP_5.Models
{
    public class Service
    {
        public int Id { get; set; } // Первичный ключ
        public string? Name { get; set; } 
        public string? Description { get; set; } 

        // Навигационное свойство
        public ICollection<UserService> UserServices { get; set; }
    }
}
