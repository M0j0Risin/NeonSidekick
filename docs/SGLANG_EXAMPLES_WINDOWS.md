Some examples of running various models under SGLang on Windows 11 (Docker/WSL2/Ubuntu).
These were tested with Nvidia RTX5090. 32GB VRAM / 64GB system RAM.

======================================================================
Qwen3.8 27B with DSpark
======================================================================

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

======================================================================
Qwen3.8 27B with MTP (Eagle) drafter
======================================================================

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

======================================================================
Qwen3.8 27B without drafter (larger context)
======================================================================

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

======================================================================
Ornith 1.5 35B A3B
======================================================================

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

======================================================================
Ornith 1.5 9B
======================================================================

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

======================================================================
Muse Glimmer 30B
======================================================================

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
  --host 0.0.0.0 --port 30000

======================================================================
Dolphin Mistral (Venice Edition) 24B
======================================================================

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
  --host 0.0.0.0 --port 30000

======================================================================
