namespace OpenCredential.AdminWeb.Services;

public static class UserInputValidation
{
    public static void Validate(UserInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Username) || input.Username.Length > 50)
            throw new InvalidOperationException("El usuario es obligatorio y admite hasta 50 caracteres.");
        if (input.FirstName is null || input.LastName is null || input.Email is null || input.DocumentId is null ||
            input.FirstName.Length > 100 || input.LastName.Length > 100 || input.Email.Length > 200 || input.DocumentId.Length > 15)
            throw new InvalidOperationException("Revisa nombres, apellidos, correo y documento; alguno supera la longitud permitida.");
        if (!string.IsNullOrEmpty(input.Email) && !System.Net.Mail.MailAddress.TryCreate(input.Email, out _))
            throw new InvalidOperationException("El correo no tiene un formato valido.");
        if (input.GroupIds?.Count > 256 || input.GroupIds?.Any(id => id <= 0) == true)
            throw new InvalidOperationException("La seleccion de grupos no es valida.");
        if (!string.IsNullOrEmpty(input.Password)) PasswordHashService.ValidateNewPassword(input.Password);
    }
}
