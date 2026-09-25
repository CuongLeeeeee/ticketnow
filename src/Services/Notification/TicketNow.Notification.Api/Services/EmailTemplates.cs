using System.Globalization;

namespace TicketNow.Notification.Api.Services;

public static class EmailTemplates
{
    private static readonly CultureInfo Vi = new("vi-VN");

    public static string Money(decimal amount, string currency) =>
        $"{amount.ToString("#,##0", Vi)} {currency}";

    public static string Layout(string heading, string body, string accent = "#1D9E75") => $"""
        <div style="font-family:system-ui,sans-serif;max-width:560px;margin:0 auto;color:#2C2C2A">
          <h2 style="color:{accent};margin:0 0 16px">{heading}</h2>
          {body}
          <hr style="border:none;border-top:1px solid #D3D1C7;margin:24px 0">
          <p style="font-size:12px;color:#5F5E5A">TicketNow — email tự động, vui lòng không trả lời.</p>
        </div>
        """;
}