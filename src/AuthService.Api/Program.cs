var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

app.MapGet("/", () => "AuthService");

app.Run();

// Exposed so integration tests (TST-02) can boot the API in-process via WebApplicationFactory<Program>.
public partial class Program;
