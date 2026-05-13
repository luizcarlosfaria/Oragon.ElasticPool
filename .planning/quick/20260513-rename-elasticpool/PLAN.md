---
status: complete
created: 2026-05-13
---

# Rename Oragon.AdaptivePool to Oragon.ElasticPool

Implement a breaking rename from `Oragon.AdaptivePool` to `Oragon.ElasticPool`, including public API names that contain `Adaptive`, package/project names, telemetry identity, docs, samples, tests, and GitHub Actions.

All projects in the main solution must target `net10.0;net9.0;net8.0`, including samples, benchmark, stress, integration, and Aspire demo projects. GitHub Actions must restore/build/test/pack using the renamed solution, paths, package filters, and NuGet package names.
