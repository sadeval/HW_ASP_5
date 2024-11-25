namespace HW_ASP_5.Models
{
    public class UpdateRegistrationRequest
    {
        public string? UserName { get; set; } 
        public string? Email { get; set; } 
        public string? PhoneNumber { get; set; } 
        public List<int> ServiceIds { get; set; } 
        public Dictionary<int, string> AdditionalInfo { get; set; } 
    }
}
