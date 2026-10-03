Some examples of running various models under vLLM on Windows 11 (Docker/WSL2/Ubuntu).
These were tested with Nvidia RTX5090. 32GB VRAM / 64GB system RAM.

======================================================================
Qwen3.6 27B
======================================================================

docker run --name vllm_qwn38_27b_mtp_1x_131072 \
    --runtime nvidia \
    --gpus all \
    -v ~/.cache/huggingface:/root/.cache/huggingface \
    --env "HF_TOKEN=YOUR_HUGGING_FACE_TOKEN" \
    -p 8000:8000 \
    --ipc=host \
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

======================================================================
Qwen3.6 35B A3B (uncensored)
======================================================================

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
