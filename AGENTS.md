# Repository Guidelines

## Project Structure & Module Organization

This repository is a .NET solution for a local HTTP proxy that exposes Ollama-compatible endpoints and forwards requests to OpenAI, Anthropic, or Ollama-style upstreams. Main application code lives in `OllamaHub/`, with the entry point in `Program.cs`. Core areas are split by responsibility: `Configuration/` for settings loading and models, `Contracts/` for request and response DTOs, `Services/` for protocol mapping and upstream clients, `Logging/` for file logging, and `Interop/` for Windows console behavior. Tests live in `OllamaHub.Tests/` and mirror the production namespaces where practical.

## Build, Test, and Development Commands

- `dotnet restore OllamaHub.slnx`: restore project and test dependencies.
- `dotnet build OllamaHub.slnx`: compile the app and test project.
- `dotnet test OllamaHub.slnx`: run the xUnit test suite.
- `dotnet run --project OllamaHub/OllamaHub.csproj`: run the proxy locally. Provide a `settings.json` next to the executable for real provider configuration.

## Coding Style & Naming Conventions

Follow `.editorconfig`: CRLF line endings, UTF-8 BOM, spaces, and 4-space indentation for code. XML, project files, and JSON use 2-space indentation. Nullable reference types and implicit usings are enabled, so prefer explicit null handling and concise using declarations. Use PascalCase for public types and members, camelCase for locals and parameters, and suffix asynchronous methods with `Async`.

## Testing Guidelines

Tests use xUnit with `Microsoft.NET.Test.Sdk` and `coverlet.collector`. Place new tests under `OllamaHub.Tests/` in folders matching the production area, for example `Services/AnthropicRequestFactoryTests.cs`. Name test methods descriptively around behavior, such as `CreateRequest_MapsSystemMessages`. Run `dotnet test OllamaHub.slnx` before submitting changes.

## Commit & Pull Request Guidelines

Recent commits use concise Chinese messages with a short type prefix, for example `Bug, ...`, `Fea, ...`, or `Bug #1, ...`. Keep commits focused on one change. Pull requests should describe the user-facing impact, list validation commands run, link related issues, and include screenshots or sample requests when behavior changes.

## Security & Configuration Tips

Do not commit real API keys, encrypted secrets, logs, or local `settings.json` files. When touching proxy behavior, avoid logging sensitive headers or request bodies unless explicitly redacted.
