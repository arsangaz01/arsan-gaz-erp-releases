namespace ArsanGazERP.Models;

public static class AppSettings
{
    public const string ClientId = "3eaf1f8a-fbc8-40f0-9759-e134542606bf";
    public const string RedirectUri = "http://localhost";

    public static readonly string[] GraphScopes =
    {
        "User.Read",
        "Files.ReadWrite",
        "Mail.Read",
        "Mail.Send",
        "Contacts.Read",
        "Calendars.ReadWrite"
    };
}
