using System;
using UnityEngine;

namespace VRPerception.Perception
{
    /// <summary>
    /// vLLM Provider 实现（使用 OpenAI 兼容接口）
    /// </summary>
    public class VLLMProvider : OpenAIProvider
    {
        public override string ProviderType => "local_vllm";
        public override string ProviderName => "vLLM";

        protected ProviderConfig Config { get; }

        public VLLMProvider(ProviderConfig config) : base(PrepareConfig(config))
        {
            Config = config;
        }

        private static ProviderConfig PrepareConfig(ProviderConfig config)
        {
            // vLLM 使用 OpenAI 兼容的接口
            // 默认端点通常是 http://localhost:8000/v1/chat/completions
            if (string.IsNullOrEmpty(config.endpoint))
            {
                config.endpoint = "http://localhost:8000/v1/chat/completions";
            }

            // vLLM 通常不需要 API key
            // 为避免误用环境变量 OPENAI_API_KEY，这里保持为空字符串，从而不发送 Authorization 头
            if (string.IsNullOrEmpty(config.apiKey))
            {
                config.apiKey = "";
            }

            return config;
        }
    }
}
