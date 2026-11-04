using AgentForge.Execution;

namespace AgentForge.Tests;

/// <summary>Regras puras do container por tarefa: o que o host agent aceita e os argumentos do `docker run`.</summary>
public class ContainerRulesTests
{
    private static ContainerSpec Spec(string image = "agentforge/stack:abc", string network = "bridge", double cpu = 2, int mem = 4096, string mount = "/data/repos/p1") =>
        new(image, null, network, cpu, mem, mount);

    [Fact]
    public void The_image_tag_follows_the_dockerfile_content()
    {
        var a = ContainerRules.ImageTag("FROM node:22\n");
        Assert.StartsWith("agentforge/stack:", a);
        Assert.Equal(a, ContainerRules.ImageTag("FROM node:22\r\n"));          // fim de linha não muda a tag
        Assert.NotEqual(a, ContainerRules.ImageTag("FROM node:20\n"));         // editar o perfil reconstrói a imagem
    }

    [Theory]
    [InlineData("node:22-bookworm", true)]
    [InlineData("minha/imagem:1.0", true)]
    [InlineData("ghcr.io/org/img@sha256:abc", true)]
    [InlineData("-v", false)]
    [InlineData("--privileged", false)]
    [InlineData("", false)]
    [InlineData("img name", false)]
    [InlineData("IMG", false)]
    [InlineData("img;rm", false)]
    public void Validates_image_names(string image, bool valid) => Assert.Equal(valid, ContainerRules.IsValidImageName(image));

    [Fact]
    public void Built_images_always_pass_and_others_need_the_hosts_list()
    {
        Assert.True(ContainerRules.ImageAllowed("agentforge/stack:123", []));
        Assert.False(ContainerRules.ImageAllowed("node:22", []));
        Assert.True(ContainerRules.ImageAllowed("node:22", ["node:22"]));
        Assert.True(ContainerRules.ImageAllowed("minha/api:3", ["minha/*"]));
        Assert.False(ContainerRules.ImageAllowed("outra/api:3", ["minha/*"]));
        Assert.False(ContainerRules.ImageAllowed("--privileged", ["*"]));   // nome inválido nunca passa, nem com curinga
    }

    [Fact]
    public void Validates_network_limits_and_the_mounted_folder()
    {
        Assert.Null(ContainerRules.Validate(Spec(), "/data/repos/p1/worktrees/t1", isWindowsHost: false));
        Assert.NotNull(ContainerRules.Validate(Spec(), "/data/repos/p1/worktrees/t1", isWindowsHost: true));
        Assert.NotNull(ContainerRules.Validate(Spec(network: "host"), "/data/repos/p1", false));
        Assert.NotNull(ContainerRules.Validate(Spec(cpu: -1), "/data/repos/p1", false));
        Assert.NotNull(ContainerRules.Validate(Spec(mem: -5), "/data/repos/p1", false));
        Assert.NotNull(ContainerRules.Validate(Spec(mount: "/data/repos/p1/../x"), "/data/repos/p1/x", false));
        Assert.NotNull(ContainerRules.Validate(Spec(mount: "/a:/etc"), "/a:/etc", false));          // injeção de segundo volume
        Assert.NotNull(ContainerRules.Validate(Spec(mount: "relativo/p1"), "relativo/p1", false));
        Assert.NotNull(ContainerRules.Validate(Spec(), "/data/repos/outro-projeto", false));         // fora da pasta montada
        Assert.NotNull(ContainerRules.Validate(Spec(), "/data/repos/p1-irmao", false));              // prefixo que não é subpasta
    }

    [Fact]
    public void The_docker_run_arguments_are_locked_down()
    {
        var args = ContainerRules.RunArguments(Spec(cpu: 1.5, mem: 2048), "af-1", "/data/repos/p1/worktrees/t1", "claude", ["-p", "--model", "sonnet"],
            "/home/eu/.claude", user: "1000:1000", passApiKey: false);
        var line = string.Join(' ', args);

        Assert.Equal(["run", "--rm", "-i"], args.Take(3));
        Assert.DoesNotContain("--privileged", args);
        Assert.DoesNotContain("docker.sock", line);
        Assert.Contains("--cap-drop ALL", line);
        Assert.Contains("--security-opt no-new-privileges", line);
        Assert.Contains("--cpus 1.5", line);
        Assert.Contains("--memory 2048m", line);
        Assert.Contains("--network bridge", line);
        Assert.Contains("--user 1000:1000", line);
        Assert.Contains("-v /data/repos/p1:/data/repos/p1", line);            // só a pasta do projeto...
        Assert.Contains("-v /home/eu/.claude:/agent-config", line);           // ...e o login do agente
        Assert.Equal(2, args.Count(a => a == "-v"));
        Assert.DoesNotContain("ANTHROPIC_API_KEY", line);
        // a imagem vem logo antes do comando do agente: nada do que o agente recebe vira opção do docker
        var imageAt = args.IndexOf("agentforge/stack:abc");
        Assert.Equal(["claude", "-p", "--model", "sonnet"], args.Skip(imageAt + 1));
    }

    [Fact]
    public void Without_limits_or_user_the_flags_are_omitted_and_the_api_key_is_forwarded_by_name_only()
    {
        var args = ContainerRules.RunArguments(Spec(network: "none", cpu: 0, mem: 0), "af-1", "/data/repos/p1", "claude", [], "/c", user: null, passApiKey: true);

        Assert.DoesNotContain("--cpus", args);
        Assert.DoesNotContain("--memory", args);
        Assert.DoesNotContain("--user", args);
        Assert.Contains("none", args);
        Assert.Contains("ANTHROPIC_API_KEY", args);                            // sem "=valor": o docker copia do ambiente
        Assert.DoesNotContain(args, a => a.StartsWith("ANTHROPIC_API_KEY="));
    }
}
