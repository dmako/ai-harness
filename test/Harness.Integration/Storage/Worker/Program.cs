using Harness.Infrastructure.Storage;

using var database = HarnessDatabase.Open(args[1]);
switch (args[0])
{
    case "put":
        database.PutDiagnostic(args[2], args[3]);
        break;
    case "hold":
        database.WithTransaction(
            (connection, transaction) =>
            {
                Console.WriteLine("LOCKED");
                Console.Out.Flush();
                Console.ReadLine();
                return 0;
            }
        );
        break;
    default:
        throw new ArgumentException("Unknown worker operation.");
}

Console.WriteLine(database.SchemaVersion());
