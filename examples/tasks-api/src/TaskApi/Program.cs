using System.Text.Json.Serialization;
using TaskApi;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<TaskStore>();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

var app = builder.Build();

app.MapGet("/tasks", (TaskStore store) => store.All());

app.MapGet("/tasks/{id:int}", (int id, TaskStore store) =>
    store.Find(id) is { } task ? Results.Ok(task) : Results.NotFound());

app.MapPost("/tasks", (CreateTaskRequest request, TaskStore store) =>
{
    if (string.IsNullOrWhiteSpace(request.Title))
        return Results.BadRequest(new { error = "O título é obrigatório." });

    var task = store.Add(request.Title.Trim(), request.Status);
    return Results.Created($"/tasks/{task.Id}", task);
});

app.Run();

public partial class Program;
