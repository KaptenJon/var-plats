var builder = DistributedApplication.CreateBuilder(args);
// "database" (lokala skrivningar utan konton) eller "konton" (syntetiska testkonton och alla brytare).
var webProfile = builder.Configuration["VarPlats:WebLaunchProfile"] ?? "database";
if (webProfile is not ("database" or "konton"))
    throw new InvalidOperationException("VarPlats:WebLaunchProfile måste vara \"database\" eller \"konton\".");
var postgres = builder.AddPostgres("postgres")
    .WithDockerfile("../../deploy/postgis")
    .WithEnvironment("POSTGRES_DB", "varplats")
    .WithDataVolume()
    .WithBindMount("../../deploy/sql", "/docker-entrypoint-initdb.d");
var database = postgres.AddDatabase("varplats");
builder.AddDotnetProject(
        "web",
        "../VarPlats.Web/VarPlats.Web.csproj",
        options => options.LaunchProfileName = webProfile)
    .WithReference(database)
    .WaitFor(database);
builder.Build().Run();
