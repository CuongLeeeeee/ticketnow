dotnet build
if ($LASTEXITCODE -ne 0) { exit 1 }

$services = @(
    @{ Name = "catalog";  Project = "src/Services/Catalog/TicketNow.Catalog.Api";   Url = "http://localhost:5001" },
    @{ Name = "identity"; Project = "src/Services/Identity/TicketNow.Identity.Api"; Url = "http://localhost:5002" },
    @{ Name = "booking"; Project = "src/Services/Booking/TicketNow.Booking.Api";    Url = "http://localhost:5003" },
    @{ Name = "payment"; Project = "src/Services/Payment/TicketNow.Payment.Api";    Url = "http://localhost:5004" },
    @{ Name = "notification"; Project = "src/Services/Notification/TicketNow.Notification.Api"; Url = "http://localhost:5005" },
    @{ Name = "gateway";  Project = "src/ApiGateway/TicketNow.Gateway";             Url = "http://localhost:5000" }
    
)

foreach ($s in $services) {
    Start-Process pwsh -ArgumentList "-NoExit", "-Command",
        "`$host.UI.RawUI.WindowTitle = '$($s.Name)'; dotnet run --no-build --project $($s.Project) --urls $($s.Url)"
}