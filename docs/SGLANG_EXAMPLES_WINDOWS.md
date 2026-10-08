# SGLang Examples (Windows)

Working `docker run` commands for serving models with [SGLang](https://github.com/sgl-project/sglang) on
Windows 11, through Docker Desktop on WSL2 (Ubuntu).

> **Tested on:** NVIDIA RTX 5090 (32 GB VRAM), 64 GB system RAM.

Not for Macs: Docker servers need Windows and an NVIDIA GPU. Back to the [README](../README.md).

## Prerequisites

- Docker Desktop with the WSL2 backend and GPU support
- A Hugging Face token: replace `YOUR_HUGGING_FACE_TOKEN` in each command
- Run the commands from a WSL (Ubuntu) shell, not PowerShell; they use `~` and `\` line continuations

Every example serves on port `30000`, so only one can run at a time. The container names record the model,
the drafter, the GPU count and the context length.

## Using them in NeonSidekick

Each container can be a `/server` choice: tick it in *Docker server containers* on `/settings` › Docker and
turn on *Docker servers enabled*. The app stops whichever one is running and starts the one you pick, so the
shared port is never a problem. See [Docker servers](TOOLS.md#docker-servers).

## Models

| Model | Container | Context | Drafter |
| --- | --- | --- | --- |
| [Qwen3.8 27B](#qwen38-27b-with-dspark-drafter) | `sglang_qwen38_27b_dspk_1x_151427` | 151,427 | DSpark |
| [Qwen3.8 27B](#qwen38-27b-with-mtp-eagle-drafter) | `sglang_qwen38_27b_eagle_1x_151424` | 151,424 | MTP (Eagle) |
| [Qwen3.8 27B](#qwen38-27b-without-a-drafter) | `sglang_qwen38_27b_nrm_1x_262144` | 262,144 | none |
| [Ornith 1.5 35B A3B](#ornith-15-35b-a3b) | `sglang_ornith15_35b_a3b_1x_262144` | 262,144 | none |
| [Ornith 1.5 9B](#ornith-15-9b) | `sglang_ornith15_9b_1x_262144` | 262,144 | none |
| [Muse Glimmer 30B](#muse-glimmer-30b) | `sglang_muse_glimmer_30b_65536` | 65,536 | none |
| [Dolphin Mistral 24B (Venice Edition)](#dolphin-mistral-24b-venice-edition) | `sglang_dolphin_mistral_ve_24b_32768_unc` | 32,768 | none |

---

## Qwen3.8 27B with DSpark drafter

```bash
docker run \
  --name sglang_qwen38_27b_dspk_1x_151427 \
  --gpus all \
  --ipc=host \
  --shm-size 32g \
  -p 30000:30000 \
  -v ~/.cache/huggingface:/root/.cache/huggingface \
  --env "HF_TOKEN=YOUR_HUGGING_FACE_TOKEN" \
  lmsysorg/sglang:qwen38-27b \
  sglang serve \
    --model-path gittensor-model-hub/Qwen3.8-27B-NVFP4-RTX5090 \
    --quantization modelopt_fp4 \
    --trust-remote-code \
    --tp-size 1 \
    --context-length 151427 \
    --speculative-algorithm DSPARK \
    --speculative-draft-model-path gittensor-model-hub/Qwen3.8-27B-DSpark-NVFP4 \
    --speculative-draft-model-quantization modelopt_fp4 \
    --speculative-dspark-block-size 7 \
    --kv-cache-dtype fp8_e4m3 \
    --attention-backend flashinfer \
    --mem-fraction-static 0.88 \
    --chunked-prefill-size 2048 \
    --cuda-graph-max-bs-decode 1 \
    --max-running-requests 1 \
    --max-mamba-cache-size 8 \
    --mamba-radix-cache-strategy extra_buffer_lazy \
    --mamba-ssm-dtype bfloat16 \
    --mm-feature-transport cpu \
    --reasoning-parser qwen3 \
    --tool-call-parser qwen3_coder \
    --host 0.0.0.0 \
    --port 30000
```

## Qwen3.8 27B with MTP (Eagle) drafter

The draft model is the main model itself: MTP uses its built-in prediction heads.

```bash
docker run \
  --name sglang_qwen38_27b_eagle_1x_151424 \
  --gpus all \
  --ipc=host \
  --shm-size 32g \
  -p 30000:30000 \
  -v ~/.cache/huggingface:/root/.cache/huggingface \
  --env "HF_TOKEN=YOUR_HUGGING_FACE_TOKEN" \
  lmsysorg/sglang:qwen38-27b \
  sglang serve \
    --model-path gittensor-model-hub/Qwen3.8-27B-NVFP4-RTX5090 \
    --quantization modelopt_fp4 \
    --trust-remote-code \
    --tp-size 1 \
    --speculative-algorithm EAGLE \
    --speculative-draft-model-path gittensor-model-hub/Qwen3.8-27B-NVFP4-RTX5090 \
    --speculative-draft-model-quantization modelopt_fp4 \
    --speculative-num-steps 1 \
    --speculative-eagle-topk 1 \
    --speculative-num-draft-tokens 1 \
    --context-length 151424 \
    --kv-cache-dtype fp8_e4m3 \
    --attention-backend flashinfer \
    --mem-fraction-static 0.93 \
    --chunked-prefill-size 2048 \
    --cuda-graph-max-bs-decode 1 \
    --max-running-requests 1 \
    --max-mamba-cache-size 8 \
    --mamba-radix-cache-strategy extra_buffer_lazy \
    --mamba-ssm-dtype bfloat16 \
    --mm-feature-transport cpu \
    --reasoning-parser qwen3 \
    --tool-call-parser qwen3_coder \
    --host 0.0.0.0 \
    --port 30000
```

## Qwen3.8 27B without a drafter

No drafter leaves room for the full 262k context.

```bash
docker run \
  --name sglang_qwen38_27b_nrm_1x_262144 \
  --gpus all \
  --ipc=host \
  --shm-size 32g \
  -p 30000:30000 \
  -v ~/.cache/huggingface:/root/.cache/huggingface \
  --env "HF_TOKEN=YOUR_HUGGING_FACE_TOKEN" \
  lmsysorg/sglang:qwen38-27b \
  sglang serve \
    --model-path gittensor-model-hub/Qwen3.8-27B-NVFP4-RTX5090 \
    --quantization modelopt_fp4 \
    --trust-remote-code \
    --tp-size 1 \
    --context-length 262144 \
    --kv-cache-dtype fp8_e4m3 \
    --attention-backend flashinfer \
    --mem-fraction-static 0.88 \
    --chunked-prefill-size 2048 \
    --cuda-graph-max-bs-decode 1 \
    --max-running-requests 1 \
    --max-mamba-cache-size 8 \
    --mamba-radix-cache-strategy extra_buffer_lazy \
    --mamba-ssm-dtype bfloat16 \
    --mm-feature-transport cpu \
    --reasoning-parser qwen3 \
    --tool-call-parser qwen3_coder \
    --host 0.0.0.0 \
    --port 30000
```

## Ornith 1.5 35B A3B

```bash
docker run \
  --name sglang_ornith15_35b_a3b_1x_262144 \
  --gpus all \
  --ipc=host \
  --shm-size 32g \
  -p 30000:30000 \
  -v ~/.cache/huggingface:/root/.cache/huggingface \
  --env "HF_TOKEN=YOUR_HUGGING_FACE_TOKEN" \
  lmsysorg/sglang:latest \
  sglang serve \
    --model-path ornith-ai/Ornith-1.5-35B-A3B-NVFP4 \
    --quantization modelopt_fp4 \
    --trust-remote-code \
    --context-length 262144 \
    --mem-fraction-static 0.88 \
    --chunked-prefill-size 2048 \
    --max-running-requests 1 \
    --tool-call-parser qwen3_coder \
    --reasoning-parser qwen3 \
    --host 0.0.0.0 \
    --port 30000
```

## Ornith 1.5 9B

```bash
docker run \
  --name sglang_ornith15_9b_1x_262144 \
  --gpus all \
  --ipc=host \
  --shm-size 32g \
  -p 30000:30000 \
  -v ~/.cache/huggingface:/root/.cache/huggingface \
  --env "HF_TOKEN=YOUR_HUGGING_FACE_TOKEN" \
  lmsysorg/sglang:latest \
  sglang serve \
    --model-path ornith-ai/Ornith-1.5-9B-NVFP4 \
    --quantization modelopt_fp4 \
    --trust-remote-code \
    --context-length 262144 \
    --mem-fraction-static 0.88 \
    --chunked-prefill-size 2048 \
    --max-running-requests 1 \
    --tool-call-parser qwen3_coder \
    --reasoning-parser qwen3 \
    --host 0.0.0.0 \
    --port 30000
```

## Muse Glimmer 30B

```bash
docker run \
  --name sglang_muse_glimmer_30b_65536 \
  --gpus all \
  --ipc=host \
  --shm-size 32g \
  -p 30000:30000 \
  -v ~/.cache/huggingface:/root/.cache/huggingface \
  --env "HF_TOKEN=YOUR_HUGGING_FACE_TOKEN" \
  lmsysorg/sglang:latest \
  sglang serve \
    --model-path RadixArk/Muse-Glimmer-NVFP4 \
    --reasoning-parser muse \
    --tool-call-parser muse \
    --language-model-only \
    --context-length 65536 \
    --tp-size 1 \
    --mem-fraction-static 0.88 \
    --host 0.0.0.0 \
    --port 30000
```

## Dolphin Mistral 24B (Venice Edition)

An uncensored fine-tune (`_unc` in the container name).

```bash
docker run \
  --name sglang_dolphin_mistral_ve_24b_32768_unc \
  --gpus all \
  --ipc=host \
  --shm-size 32g \
  -p 30000:30000 \
  -v ~/.cache/huggingface:/root/.cache/huggingface \
  --env "HF_TOKEN=YOUR_HUGGING_FACE_TOKEN" \
  lmsysorg/sglang:latest \
  sglang serve \
    --model-path Firworks/Dolphin-Mistral-24B-Venice-Edition-nvfp4 \
    --quantization compressed-tensors \
    --tool-call-parser mistral \
    --context-length 32768 \
    --tp-size 1 \
    --mem-fraction-static 0.88 \
    --host 0.0.0.0 \
    --port 30000
```
