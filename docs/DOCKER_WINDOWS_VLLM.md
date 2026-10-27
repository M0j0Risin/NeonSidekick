# vLLM Examples (Windows)

Working `docker run` commands for serving models with [vLLM](https://github.com/vllm-project/vllm) on Windows 11, through Docker Desktop on WSL2 (Ubuntu).

> **Tested on:** NVIDIA RTX 5090 (32 GB VRAM), 64 GB system RAM.

Not for Macs: Docker servers need Windows and an NVIDIA GPU. Back to the [README](../README.md).

## Prerequisites

- An NVIDIA GPU, with WSL 2, Docker Desktop and the GPU set up as in [Install](#install)
- A Hugging Face token: replace `YOUR_HUGGING_FACE_TOKEN` in each command
- Run the commands from a WSL (Ubuntu) shell, not PowerShell; they use `~` and `\` line continuations

Every example serves on port `8000`, so only one can run at a time. Each container name records the model, drafter, GPU count and context length.

## Install

Once per machine; skip what you've done. Steps 1–4 are in Windows, the rest in the Ubuntu shell.

1. **NVIDIA driver.** Install the current Windows driver for your GPU (from nvidia.com or the NVIDIA app). It serves WSL too. Don't install an NVIDIA driver or the CUDA toolkit inside Ubuntu: either would replace the one WSL uses.
2. **WSL 2 and Ubuntu.** In PowerShell as administrator:

   ```powershell
   wsl --install -d Ubuntu
   wsl --update
   ```

   Restart if asked, open Ubuntu from the Start menu and create your Linux user. `wsl -l -v` should show Ubuntu at version 2.
3. **Docker Desktop.** Install it from [docker.com](https://www.docker.com/products/docker-desktop/). In Settings › General, tick *Use WSL 2 based engine*; in Settings › Resources › WSL Integration, turn on Ubuntu; then *Apply & restart*.
4. **WSL memory.** WSL 2 gives Linux half the system RAM by default, and the containers run inside it. The examples use a lot of shared memory, and a model's weights pass through system RAM as it loads. On a 64 GB machine, raise it in `%UserProfile%\.wslconfig`:

   ```ini
   [wsl2]
   memory=48GB
   ```

   Then run `wsl --shutdown` in PowerShell and restart Docker Desktop.
5. **Check the GPU** in the Ubuntu shell. It should name your GPU and print its GFLOP/s:

   ```bash
   docker run --rm --gpus=all nvcr.io/nvidia/k8s/cuda-sample:nbody nbody -gpu -benchmark
   ```

6. **Hugging Face cache.** Create the folder the examples mount now, so Docker doesn't create it owned by root:

   ```bash
   mkdir -p ~/.cache/huggingface
   ```

   Models download here on their first run, tens of GB each, onto Ubuntu's virtual disk.
7. **Pull the image.** `docker run` would pull it anyway, but it's large:

   ```bash
   docker pull vllm/vllm-openai:latest
   ```

   To update later, pull again, remove the container (`docker rm <name>`) and run its command again: an existing container keeps its old image.

## Using them in NeonSidekick

1. Run an example's command once to create its container (the first run downloads the model). From then on the app starts and stops it.
2. On `/settings` › Servers › Docker, turn on *Docker servers enabled* and tick the container in *Docker server containers*. The list shows containers whose image or command has one of the *Docker server match words* (`vllm` is one by default).
3. Pick the container in `/server`, or start the app with `--url docker:<container>`.

The app starts the container you pick and stops the other ticked ones first, so the shared port is never a problem. See [Docker servers](TOOLS.md#docker-servers).

## Models

| Model | Container | Context | Drafter |
| --- | --- | --- | --- |
| [Qwen3.8 27B](#qwen38-27b-with-mtp) | `vllm_qwn38_27b_mtp_1x_131072` | 131,072 | MTP |
| [Qwen3.6 35B A3B (uncensored)](#qwen36-35b-a3b-uncensored) | `vllm_qwn36_35b_a3b_1x_131072_unc` | 131,072 | none |

---

## Qwen3.8 27B with MTP

```bash
docker run \
  --name vllm_qwn38_27b_mtp_1x_131072 \
  --runtime nvidia \
  --gpus all \
  --ipc=host \
  -p 8000:8000 \
  -v ~/.cache/huggingface:/root/.cache/huggingface \
  --env "HF_TOKEN=YOUR_HUGGING_FACE_TOKEN" \
  vllm/vllm-openai:latest \
    --model gittensor-model-hub/Qwen3.8-27B-NVFP4-RTX5090 \
    --host 0.0.0.0 \
    --port 8000 \
    --trust-remote-code \
    --quantization modelopt \
    --dtype bfloat16 \
    --kv-cache-dtype fp8 \
    --gpu-memory-utilization 0.94 \
    --max-model-len 131072 \
    --max-num-seqs 1 \
    --speculative-config.method mtp \
    --speculative-config.num_speculative_tokens 2 \
    --enable-chunked-prefill \
    --max-num-batched-tokens 2048 \
    --enable-prefix-caching \
    --enable-auto-tool-choice \
    --tool-call-parser qwen3_xml \
    --reasoning-parser qwen3 \
    --chat-template-content-format openai
```

## Qwen3.6 35B A3B (uncensored)

`VLLM_NVFP4_GEMM_BACKEND=marlin` selects the Marlin kernels for the NVFP4 weights.

```bash
docker run \
  --name vllm_qwn36_35b_a3b_1x_131072_unc \
  --runtime nvidia \
  --gpus all \
  --ipc=host \
  -p 8000:8000 \
  -v ~/.cache/huggingface:/root/.cache/huggingface \
  --env "HF_TOKEN=YOUR_HUGGING_FACE_TOKEN" \
  --env "VLLM_NVFP4_GEMM_BACKEND=marlin" \
  vllm/vllm-openai:latest \
    lyf/Qwen3.6-35B-A3B-Uncensored-HauhauCS-Aggressive-NVFP4 \
    --host 0.0.0.0 \
    --port 8000 \
    --quantization compressed-tensors \
    --kv-cache-dtype fp8 \
    --max-model-len 131072 \
    --max-num-seqs 1 \
    --max-num-batched-tokens 4096 \
    --gpu-memory-utilization 0.90 \
    --enable-prefix-caching \
    --enable-auto-tool-choice \
    --tool-call-parser qwen3_coder \
    --reasoning-parser qwen3
```
