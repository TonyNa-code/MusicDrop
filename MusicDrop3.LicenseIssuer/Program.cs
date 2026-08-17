using MFlacDrop;

static string Need(Dictionary<string, string> values, string key) =>
    values.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value)
        ? value : throw new ArgumentException("Missing --" + key);

var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
for (int i = 1; i + 1 < args.Length; i += 2)
{
    if (!args[i].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException("Unexpected argument: " + args[i]);
    values[args[i][2..]] = args[i + 1];
}

if (args.Length > 0 && args[0].Equals("generate-key", StringComparison.OrdinalIgnoreCase))
{
    string privatePath = Path.GetFullPath(Need(values, "private"));
    string publicPath = Path.GetFullPath(Need(values, "public"));
    if (File.Exists(privatePath) || File.Exists(publicPath))
        throw new IOException("Refusing to overwrite an existing key file.");
    (string privatePem, string publicKey) = RetailLicenseService.GenerateKeyPair();
    Directory.CreateDirectory(Path.GetDirectoryName(privatePath)!);
    Directory.CreateDirectory(Path.GetDirectoryName(publicPath)!);
    File.WriteAllText(privatePath, privatePem);
    File.WriteAllText(publicPath, publicKey + Environment.NewLine);
    Console.WriteLine("PRIVATE " + privatePath);
    Console.WriteLine("PUBLIC " + publicPath);
    Console.WriteLine("Keep the private key offline and never commit or ship it.");
    return;
}

if (args.Length > 0 && args[0].Equals("issue", StringComparison.OrdinalIgnoreCase))
{
    string keyPath = Path.GetFullPath(Need(values, "key"));
    string outputPath = Path.GetFullPath(Need(values, "out"));
    if (File.Exists(outputPath)) throw new IOException("Refusing to overwrite an existing license file.");
    var payload = new BuyerLicensePayload(
        SchemaVersion: 1,
        Product: "MusicDrop",
        Edition: "Convenience",
        Buyer: Need(values, "buyer"),
        OrderId: Need(values, "order"),
        IssuedDate: values.GetValueOrDefault("date") ?? DateOnly.FromDateTime(DateTime.Today).ToString("yyyy-MM-dd"),
        Permanent: true);
    string document = RetailLicenseService.CreateSignedDocument(payload, File.ReadAllText(keyPath));
    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
    File.WriteAllText(outputPath, document);
    Console.WriteLine("LICENSE " + outputPath);
    return;
}

Console.Error.WriteLine("Usage:\n  generate-key --private <pem> --public <txt>\n  issue --key <pem> --out <json> --buyer <name> --order <id> [--date yyyy-MM-dd]");
Environment.ExitCode = 64;
