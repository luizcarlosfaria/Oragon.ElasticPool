---
status: complete
completed: 2026-05-13
---

# Summary

Renamed the repository identity from `Oragon.AdaptivePool` to `Oragon.ElasticPool`, including public API symbols, namespaces, project/package names, samples, tests, documentation, telemetry names, and GitHub Actions paths.

All projects now target `net10.0;net9.0;net8.0`. The GitHub build and release workflows now restore/build both the main solution and the LiveDashboard sample solution across the matrix.

Validation completed:

- `dotnet build Oragon.ElasticPool.slnx -c Release --no-restore -f net8.0`
- `dotnet build Oragon.ElasticPool.slnx -c Release --no-restore -f net9.0`
- `dotnet build Oragon.ElasticPool.slnx -c Release --no-restore -f net10.0`
- `dotnet build samples/Oragon.ElasticPool.RabbitMQ.Sample.LiveDashboard/Oragon.ElasticPool.RabbitMQ.Sample.LiveDashboard.slnx -c Release --no-restore -f net8.0`
- `dotnet build samples/Oragon.ElasticPool.RabbitMQ.Sample.LiveDashboard/Oragon.ElasticPool.RabbitMQ.Sample.LiveDashboard.slnx -c Release --no-restore -f net9.0`
- `dotnet build samples/Oragon.ElasticPool.RabbitMQ.Sample.LiveDashboard/Oragon.ElasticPool.RabbitMQ.Sample.LiveDashboard.slnx -c Release --no-restore -f net10.0`
- `dotnet test --solution Oragon.ElasticPool.slnx --configuration Release --no-build -f net8.0`
- `dotnet test --solution Oragon.ElasticPool.slnx --configuration Release --no-build -f net9.0`
- `dotnet test --solution Oragon.ElasticPool.slnx --configuration Release --no-build -f net10.0`
- `dotnet pack` for Core and RabbitMQ produced `.nupkg` and `.snupkg` packages under `/tmp/elastic-pack`.
