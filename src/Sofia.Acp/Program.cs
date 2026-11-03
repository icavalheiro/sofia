using Sofia.Acp;

var baseUrl = Environment.GetEnvironmentVariable("SOFIA_SERVER_URL");
var client = new SofiaClient(baseUrl);
var launcher = new ServerLauncher(baseUrl);
var conn = new AcpConnection(Console.OpenStandardInput(), Console.OpenStandardOutput());
var agent = new AcpAgent(conn, client, launcher);

conn.Start(CancellationToken.None);

try
{
    await conn.ReadTask;
}
catch (OperationCanceledException) { }

await conn.DisposeAsync();
launcher.Stop();
await client.DisposeAsync();