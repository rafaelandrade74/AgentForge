using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace TaskApi.Tests;

public class TaskEndpointsTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task List_returns_all_tasks()
    {
        var client = factory.CreateClient();

        var tasks = await client.GetFromJsonAsync<JsonElement>("/tasks");

        Assert.Equal(3, tasks.GetArrayLength());
    }

    [Fact]
    public async Task Get_by_id_returns_the_task_or_404()
    {
        var client = factory.CreateClient();

        var found = await client.GetAsync("/tasks/2");
        var missing = await client.GetAsync("/tasks/999");

        Assert.Equal(HttpStatusCode.OK, found.StatusCode);
        Assert.Equal("InProgress", (await found.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Create_returns_201_and_the_task_shows_up_in_the_list()
    {
        var client = factory.WithWebHostBuilder(_ => { }).CreateClient();

        var response = await client.PostAsJsonAsync("/tasks", new { title = "Nova tarefa", status = "Todo" });
        var created = await response.Content.ReadFromJsonAsync<JsonElement>();
        var tasks = await client.GetFromJsonAsync<JsonElement>("/tasks");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Contains(tasks.EnumerateArray(), t => t.GetProperty("id").GetInt32() == created.GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task Create_without_title_returns_400()
    {
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/tasks", new { title = " " });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
