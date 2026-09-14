var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
app.MapGet("/", () => "Marten Studio sample host - placeholder, replaced in a later packet.");
app.Run();
