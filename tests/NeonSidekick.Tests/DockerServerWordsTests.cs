using NeonSidekick.Docker;
using NeonSidekick.Settings;

namespace NeonSidekick.Tests;

/// <summary>Which containers are LLM servers (2026-10-10): the word list, the label, the command the list reads.</summary>
public class DockerServerWordsTests
{
    private static DockerContainer Container(string image, string command = "", IReadOnlyDictionary<string, string>? labels = null) =>
        new("id", "c", image, "sha", "running", "Up", 0, [], labels ?? new Dictionary<string, string>(), [], [], command);

    [Theory]
    [InlineData("vllm/vllm-openai:latest")]
    [InlineData("lmsysorg/sglang:v0.5.2-cu126")]
    [InlineData("ghcr.io/ggml-org/llama.cpp:server-cuda")]
    [InlineData("ollama/ollama:latest")]
    [InlineData("ghcr.io/huggingface/text-generation-inference:3.0")]
    [InlineData("localai/localai:latest-gpu-nvidia-cuda-12")]
    [InlineData("openmmlab/lmdeploy:latest")]
    [InlineData("nvcr.io/nvidia/tritonserver:24.08-trtllm-python-py3")]
    [InlineData("nvcr.io/nim/meta/llama-3.1-8b-instruct:latest")]
    [InlineData("alpindale/aphrodite-openai:latest")]
    [InlineData("KoboldAI/KoboldCpp")]
    public void EachKnownEngine_MatchesByItsImage(string image)
    {
        Assert.True(DockerServerWords.Matches(Container(image), DockerServerWords.Defaults));
    }

    [Fact]
    public void AnEngine_MatchesByItsCommand_WhenTheImageSaysNothing()
    {
        Assert.True(DockerServerWords.Matches(Container("python:3.12", "python3 -m sglang.launch_server --model-path Qwen/Qwen3-8B"), DockerServerWords.Defaults));
        Assert.True(DockerServerWords.Matches(Container("my/server:1", "VLLM serve Qwen/Qwen3-8B"), DockerServerWords.Defaults));   // case ignored
        Assert.True(DockerServerWords.Matches(Container("my/server:1", "/app/llama-server -m /models/x.gguf"), DockerServerWords.Defaults));
    }

    [Fact]
    public void TheLabel_MakesAnyContainerAServer_WhateverTheWords()
    {
        Assert.Equal("neonsidekick.llm", DockerServerWords.LlmLabel);
        var labelled = Container("acme/runner", labels: new Dictionary<string, string> { [DockerServerWords.LlmLabel] = " True " });
        Assert.True(DockerServerWords.Matches(labelled, DockerServerWords.Defaults));
        Assert.True(DockerServerWords.Matches(labelled, []));
        Assert.False(DockerServerWords.Matches(Container("acme/runner", labels: new Dictionary<string, string> { [DockerServerWords.LlmLabel] = "false" }), DockerServerWords.Defaults));
    }

    [Fact]
    public void ADatabase_IsNotAServer_AndABlankWordMatchesNothing()
    {
        Assert.False(DockerServerWords.Matches(Container("mysql:8.4", "docker-entrypoint.sh mysqld"), DockerServerWords.Defaults));
        Assert.False(DockerServerWords.Matches(Container("postgres:17", "docker-entrypoint.sh postgres"), DockerServerWords.Defaults));
        Assert.False(DockerServerWords.Matches(Container("mcr.microsoft.com/mssql/server:2022-latest"), DockerServerWords.Defaults));
        Assert.False(DockerServerWords.Matches(Container("vllm/vllm-openai"), ["", "  "]));
        Assert.False(DockerServerWords.Matches(Container("vllm/vllm-openai"), []));
        Assert.True(DockerServerWords.Matches(Container("acme/mysql-llm"), ["mysql"]));   // the user's own word
    }

    [Fact]
    public void TheDefaults_AreTheKnownEngines_AFreshListEachRead_AndAFreshProfilesList()
    {
        Assert.Equal(["aphrodite", "koboldcpp", "llama-server", "llama.cpp", "lmdeploy", "localai", "nvcr.io/nim", "ollama", "sglang", "text-generation", "trtllm", "vllm"], DockerServerWords.Defaults);
        Assert.NotSame(DockerServerWords.Defaults, DockerServerWords.Defaults);
        Assert.Equal(DockerServerWords.Defaults, new AppSettingsData().DockerServerMatchWords);
    }

    [Fact]
    public void TheList_KeepsTheUsersCase_SortedWithoutDuplicates()
    {
        Assert.Equal(["Ollama", "vllm"], DockerServerWords.Sorted([" vllm ", "Ollama", "VLLM", "", "ollama"]));
        Assert.Equal(["tgi", "vllm"], DockerServerWords.Add(["vllm"], " tgi "));
        Assert.Equal(["vllm"], DockerServerWords.Add(["vllm"], "VLLM"));
        Assert.Equal(["vllm"], DockerServerWords.Add(["vllm"], "  "));
        Assert.Equal(["vllm"], DockerServerWords.Without(["Ollama", "vllm"], " ollama"));
        Assert.True(DockerServerWords.Contains(["vllm"], " VLLM "));
        Assert.False(DockerServerWords.Contains(["vllm"], "sglang"));
    }

    [Fact]
    public void TheContainerList_CarriesTheCommand_BlankWhereTheEngineLeftItOut()
    {
        var listed = DockerJson.Containers("""
            [
              {"Id": "a1", "Names": ["/qwen"], "Image": "my/server:1", "Command": "vllm serve Qwen/Qwen3-8B", "State": "running"},
              {"Id": "b2", "Names": ["/old"], "Image": "my/other:1", "State": "exited"}
            ]
            """);

        Assert.NotNull(listed);
        Assert.Equal("vllm serve Qwen/Qwen3-8B", listed[0].Command);
        Assert.Equal("", listed[1].Command);
        Assert.True(DockerServerWords.Matches(listed[0], DockerServerWords.Defaults));
        Assert.False(DockerServerWords.Matches(listed[1], DockerServerWords.Defaults));
    }
}
