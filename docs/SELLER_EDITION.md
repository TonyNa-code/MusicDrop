# Convenience Edition seller guide

The Convenience Edition is a low-friction one-time-purchase wrapper around the same open conversion core. It does not use subscriptions, hardware fingerprints, expiry dates, online activation or audio watermarks.

Each buyer receives a signed `buyer-license.json` containing only a display name, order ID, issue date, edition and permanent flag. The app embeds the public key and verifies the signature offline. Put the file beside `MusicDrop3.exe`; the buyer normally does nothing else. If it is moved, the app offers a standard file picker once.

## Seller workflow

Generate the signing key once on an offline or well-protected machine:

```powershell
dotnet run --project MusicDrop3.LicenseIssuer -- generate-key `
  --private seller-private/musicdrop-retail-private.pem `
  --public seller-private/musicdrop-retail-public.txt
```

Compile the public key into `RetailLicenseService.OfficialPublicKeySpkiBase64`. Never commit, upload, email or package the private PEM.

Issue one permanent file per order:

```powershell
dotnet run --project MusicDrop3.LicenseIssuer -- issue `
  --key seller-private/musicdrop-retail-private.pem `
  --out buyer-license.json --buyer "买家显示名" --order "订单号"
```

Build the retail gate with:

```powershell
dotnet publish MusicDrop3/MFlacDrop.csproj -c Release -r win-x64 `
  --self-contained true -p:MusicDropRetailBuild=true
```

Open source makes a strong anti-copy DRM impossible: a determined person can rebuild without the retail gate. The signed order record discourages casual reposting while keeping normal buyers out of activation trouble. Store order data responsibly and do not put personal details beyond what is necessary in the display field.
