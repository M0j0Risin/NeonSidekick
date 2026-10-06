# vLLM Examples (Windows)

Working `docker run` commands for serving models with [vLLM](https://github.com/vllm-project/vllm) on
Windows 11, through Docker Desktop on WSL2 (Ubuntu).

> **Tested on:** NVIDIA RTX 5090 (32 GB VRAM), 64 GB system RAM.

## Prerequisites

- Docker Desktop with the WSL2 backend and GPU support
- A Hugging Face token: replace `YOUR_HUGGING_FACE_TOKEN` in each command
- Run the commands from a WSL (Ubuntu) shell, not PowerShell; they use `~` and `\` line continuations

Every example serves on port `8000`, so only one can run at a time. The container names record the model,
the drafter, the GPU count and the context length.

## Using them in NeonSidekick

Each container can be a `/server` choice: tick it in *Docker server containers* on `/settings` › Docker and
turn on *Docker servers enabled*. The app stops whichever one is running and starts the one you pick, so the
shared port is never a problem. See [Docker servers](TOOLS.md#docker-servers).

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
