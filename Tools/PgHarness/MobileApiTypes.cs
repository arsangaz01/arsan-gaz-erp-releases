namespace ArsanGazERP.MobileApi;

// Same shape as the record in MobileEndpoints.cs, which is not linked here (CustomerChoices.CreateAsync needs it).
internal sealed record CustomerCreateRequest(
    string? Name, string? ContactName, string? Phone, string? Email, string? City, string? District, string? Address,
    string? TaxOffice, string? TaxNumber, bool AllowDuplicate = false);
