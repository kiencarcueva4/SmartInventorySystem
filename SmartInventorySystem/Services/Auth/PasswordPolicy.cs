using System.Linq;

namespace SmartInventorySystem.Services.Auth
{
    public static class PasswordPolicy
    {
        public const int MinLength = 8;

        public const string Requirements =
            "At least 8 characters, with at least one letter and one number.";

        /// <summary>Returns an error message, or null when the password is acceptable.</summary>
        public static string? Validate(string? password)
        {
            if (string.IsNullOrEmpty(password) || password.Length < MinLength)
                return $"Password must be at least {MinLength} characters.";
            if (!password.Any(char.IsLetter) || !password.Any(char.IsDigit))
                return "Password must contain at least one letter and one number.";
            return null;
        }
    }
}
