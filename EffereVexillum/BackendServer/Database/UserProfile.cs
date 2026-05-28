using System.ComponentModel.DataAnnotations;
namespace BackendServer.Database;
public class UserProfile
{
    [Key]
    public string UserId { get; set; } = string.Empty;
    public string AvatarDataUrl { get; set; } = string.Empty; // using DataUrl or image identifier
}
