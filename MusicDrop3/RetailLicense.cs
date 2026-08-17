using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MFlacDrop;

internal sealed record BuyerLicensePayload(
    int SchemaVersion,
    string Product,
    string Edition,
    string Buyer,
    string OrderId,
    string IssuedDate,
    bool Permanent);

internal sealed record BuyerLicenseDocument(
    BuyerLicensePayload Payload,
    string Signature);

internal sealed record BuyerLicenseStatus(
    bool IsValid,
    string Summary,
    BuyerLicensePayload? Payload = null,
    string? Path = null);

internal static class RetailLicenseService
{
    // The private key is never compiled or committed. It is kept by the seller only.
    internal const string OfficialPublicKeySpkiBase64 = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEy7tysIFgqcmaPXKsm8UpYCSf0Jf4HZsBr4ANyITeJj/KCciyc6WG/lU9RZudxvCKKi76KBbNueVDHpInTFYsPA==";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static bool IsRetailBuild
    {
        get
        {
#if MUSICDROP_RETAIL
            return true;
#else
            return false;
#endif
        }
    }

    public static BuyerLicenseStatus GetCurrentStatus()
    {
        string[] candidates =
        {
            Path.Combine(AppContext.BaseDirectory, "buyer-license.json"),
            AppInfo.InstalledLicensePath,
        };
        BuyerLicenseStatus? invalid = null;
        foreach (string path in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(path)) continue;
            BuyerLicenseStatus status = ValidateFile(path);
            if (status.IsValid) return status;
            invalid ??= status;
        }
        return invalid ?? new BuyerLicenseStatus(false,
            IsRetailBuild ? "未找到便利版买家凭证" : "社区版（无需许可证）");
    }

    public static BuyerLicenseStatus ValidateFile(string path, string? publicKeySpkiBase64 = null)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length is <= 0 or > 64 * 1024)
                return new(false, "凭证文件不存在或大小异常", Path: path);
            BuyerLicenseDocument? document = JsonSerializer.Deserialize<BuyerLicenseDocument>(
                File.ReadAllText(path), JsonOptions);
            if (document?.Payload is null || string.IsNullOrWhiteSpace(document.Signature))
                return new(false, "凭证结构不完整", Path: path);
            string? fieldError = ValidatePayload(document.Payload);
            if (fieldError is not null) return new(false, fieldError, Path: path);

            byte[] signature = Convert.FromBase64String(document.Signature);
            if (signature.Length != 64)
                return new(false, "凭证签名长度异常", Path: path);
            string keyText = publicKeySpkiBase64 ?? OfficialPublicKeySpkiBase64;
            byte[] publicKey = Convert.FromBase64String(keyText);
            using ECDsa ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(publicKey, out int read);
            if (read != publicKey.Length || !ecdsa.VerifyData(
                CanonicalPayload(document.Payload), signature, HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                return new(false, "凭证签名无效或内容已被修改", Path: path);
            return new(true,
                $"便利版 · 永久授权给 {document.Payload.Buyer} · 订单 {document.Payload.OrderId}",
                document.Payload, path);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or CryptographicException or IOException or UnauthorizedAccessException)
        {
            return new(false, "凭证无法读取或校验：" + ex.Message, Path: path);
        }
    }

    public static BuyerLicenseStatus InstallLicense(string sourcePath)
    {
        BuyerLicenseStatus status = ValidateFile(sourcePath);
        if (!status.IsValid) return status;
        Directory.CreateDirectory(AppInfo.DataDir);
        string temp = AppInfo.InstalledLicensePath + ".installing";
        try
        {
            File.Copy(sourcePath, temp, overwrite: true);
            BuyerLicenseStatus copied = ValidateFile(temp);
            if (!copied.IsValid) return copied;
            File.Move(temp, AppInfo.InstalledLicensePath, overwrite: true);
            return ValidateFile(AppInfo.InstalledLicensePath);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    public static bool EnsureRetailLicenseInteractive()
    {
        if (!IsRetailBuild) return true;
        BuyerLicenseStatus current = GetCurrentStatus();
        if (current.IsValid) return true;
        DialogResult select = MessageBox.Show(
            "这是 MusicDrop™ 便利版，需要随订单提供的 buyer-license.json。\n\n许可证永久有效、不绑定电脑、不联网，也不会写入音频。是否现在选择许可证文件？",
            "选择买家许可证", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
        if (select != DialogResult.Yes) return false;
        using var dialog = new OpenFileDialog
        {
            Filter = "MusicDrop 买家许可证|buyer-license.json|JSON 文件|*.json",
            Title = "选择订单附带的 buyer-license.json",
        };
        if (dialog.ShowDialog() != DialogResult.OK) return false;
        BuyerLicenseStatus installed = InstallLicense(dialog.FileName);
        if (!installed.IsValid)
        {
            MessageBox.Show(installed.Summary, "许可证无效", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        MessageBox.Show(installed.Summary, "许可证已导入", MessageBoxButtons.OK, MessageBoxIcon.Information);
        return true;
    }

    internal static (string PrivateKeyPem, string PublicKeySpkiBase64) GenerateKeyPair()
    {
        using ECDsa ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (ecdsa.ExportPkcs8PrivateKeyPem(), Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo()));
    }

    internal static string CreateSignedDocument(BuyerLicensePayload payload, string privateKeyPem)
    {
        string? fieldError = ValidatePayload(payload);
        if (fieldError is not null) throw new ArgumentException(fieldError, nameof(payload));
        using ECDsa ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(privateKeyPem);
        byte[] signature = ecdsa.SignData(
            CanonicalPayload(payload), HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return JsonSerializer.Serialize(new BuyerLicenseDocument(payload, Convert.ToBase64String(signature)), JsonOptions);
    }

    private static byte[] CanonicalPayload(BuyerLicensePayload payload) =>
        JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);

    private static string? ValidatePayload(BuyerLicensePayload payload)
    {
        if (payload.SchemaVersion != 1) return "不支持的许可证版本";
        if (!string.Equals(payload.Product, "MusicDrop", StringComparison.Ordinal)) return "许可证产品不匹配";
        if (!string.Equals(payload.Edition, "Convenience", StringComparison.Ordinal)) return "许可证版本不匹配";
        if (!payload.Permanent) return "许可证不是永久许可证";
        if (string.IsNullOrWhiteSpace(payload.Buyer) || payload.Buyer.Length > 80) return "买家显示名无效";
        if (string.IsNullOrWhiteSpace(payload.OrderId) || payload.OrderId.Length > 80) return "订单号无效";
        if (!DateOnly.TryParseExact(payload.IssuedDate, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out _)) return "签发日期无效";
        return null;
    }
}
