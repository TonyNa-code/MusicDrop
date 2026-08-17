# Security policy

Please report a vulnerability privately to the maintainer before opening a public issue when it could expose local files, keys, buyer information, or enable untrusted code execution.

Include the affected version, reproduction steps, expected impact and any proof-of-concept that does not contain real account credentials or copyrighted music. Do not upload platform databases, EKeys, buyer licenses or seller private keys to public issues.

Security-sensitive design properties:

- source music is never deleted or modified in place;
- formal output is written through temporary files and validated before completion;
- downloadable tools are pinned and hash-verified;
- FFmpeg ZIP extraction rejects traversal, links, duplicate required files and oversized archives;
- the QQ client discovery path requires a valid Tencent Authenticode identity;
- the seller signing private key must never be committed or shipped.
- QMC keyring values and KGM databases are local secrets and must never be logged, uploaded or added to fixtures;
- desktop input manifests are size/count bounded and removed after the portable CLI exits;
- release FFmpeg configurations must reject GPL/nonfree drift and unexpected dynamic package-manager dependencies.

Only the latest Community release receives routine fixes. Experimental compatibility paths are best-effort and clearly isolated.
