var builder = DistributedApplication.CreateBuilder(args);
var postgres = builder.AddPostgres("postgres")
    .WithDockerfile("../../deploy/postgis")
    .WithEnvironment("POSTGRES_DB", "varplats")
    .WithDataVolume()
    .WithBindMount("../../deploy/sql", "/docker-entrypoint-initdb.d");
var database = postgres.AddDatabase("varplats");
builder.AddDotnetProject(
        "web",
        "../VarPlats.Web/VarPlats.Web.csproj",
        options => options.LaunchProfileName = "database")
    .WithReference(database)
    .WaitFor(database);
builder.Build().Run();
