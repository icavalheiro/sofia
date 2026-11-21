// The contract assembly is dependency-free, so every project that touches entities, events or the wire JSON pulls
// these namespaces in once instead of repeating the usings in ~70 files.
global using Sofia.Contracts.Domain;
global using Sofia.Contracts.Events;
global using Sofia.Contracts.Infrastructure;

// A class library does not get the Web SDK's implicit usings: the engine is full of hosted services and loggers,
// so they are global here too.
global using Microsoft.Extensions.DependencyInjection;
global using Microsoft.Extensions.Hosting;
global using Microsoft.Extensions.Logging;

// The agent layer owns the error types every host maps to 404/409.
global using Sofia.Core.Agent;

// HttpContent helpers (JsonContent, ReadFromJsonAsync) come from the Web SDK's implicit usings in the server.
global using System.Net.Http.Json;
