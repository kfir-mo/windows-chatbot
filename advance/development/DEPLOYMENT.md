# Deployment

Choose the [local edition](local/README.md) for standalone personal testing or the
[advanced edition](advanced/README.md) for centrally managed domain deployment.

For the advanced edition, follow the [internal deployment guide](advanced/server/INTERNAL-DEPLOYMENT.md).
Distribute only `advanced/client` to users. Keep `advanced/server` on the
administrator-controlled machine.

`advanced/server/.env` contains upstream credentials and token-response mappings.
`advanced/client/.env` contains the server endpoint and non-secret preferences.
Use `scripts/publish-internal.ps1` to prepare a clean managed transfer package.
