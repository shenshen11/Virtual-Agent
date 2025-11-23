using System;
using System.Text;
using UnityEngine;

namespace VRPerception.Perception
{
    /// <summary>
    /// 提示词与工具定义模板（面向多后端一致性）
    /// - 提供：系统提示词、任务提示词、工具 JSON Schema（与 ILLMProvider.ToolSpec 对齐）
    /// - 尽量保持“ONLY JSON”约束与任务输出口径一致
    /// </summary>
    public static class PromptTemplates
    {
        // ============ System Prompts ============

        public static string GetSystemPrompt(string taskId)
        {
            switch ((taskId ?? "").ToLowerInvariant())
            {
                case "distance_compression":
                    return DistanceCompressionSystem();
                case "semantic_size_bias":
                    return SemanticSizeBiasSystem();
                default:
                    return GenericSystem();
            }
        }

        private static string GenericSystem()
        {
            // 统一要求：仅输出 JSON；不足信息时可给出 action_plan，但优先直接回答
            return "You are a vision agent. ONLY output JSON for the given task. " +
                   "If information is insufficient, output an action_plan array with tool calls; otherwise output an inference JSON. " +
                   "Never output extra text.";
        }

        private static string DistanceCompressionSystem()
        {
            return "You are a vision agent for Distance Compression. ONLY output JSON. " +
                   "Inference format: {\"type\":\"inference\",\"taskId\":\"distance_compression\",\"trialId\":<int>," +
                   "\"answer\":{\"distance_m\":<number>},\"confidence\":<0..1>} " +
                   "If more views are needed, you may output {\"type\":\"action_plan\", \"actions\":[...]} with provided tools. " +
                   "Do NOT output any extra text.";
        }

        private static string SemanticSizeBiasSystem()
        {
            return "You are a vision agent for Semantic Size Bias. ONLY output JSON. " +
                   "Inference format: {\"type\":\"inference\",\"taskId\":\"semantic_size_bias\",\"trialId\":<int>," +
                   "\"answer\":{\"larger\":\"A\"|\"B\"},\"confidence\":<0..1>} " +
                   "If more information is needed, you may output {\"type\":\"action_plan\",\"actions\":[...]} with provided tools. " +
                   "Do NOT output any extra text.";
        }

        // ============ Task Prompts ============

        public static string BuildDistanceCompressionPrompt(string targetKind, float fovDeg, string environment)
        {
            var envText = (environment ?? "open_field") == "corridor" ? "a long corridor" : "an open field";
            return $"Task: Estimate the distance to the target object in meters.\n" +
                   $"Scene: {envText}. Target kind: {targetKind}. Camera FOV: {fovDeg} deg.\n" +
                   $"Output ONLY JSON with fields: type=inference, answer.distance_m (float), confidence (0..1).";
        }

        public static string BuildSemanticSizeBiasPrompt(string objectA, string objectB, string relation, string background)
        {
            var bg = string.IsNullOrEmpty(background) ? "none" : background;
            var rel = string.IsNullOrEmpty(relation) ? "equal" : relation;
            return $"Task: Decide which object is larger on screen (A or B).\n" +
                   $"Pair: {objectA} vs {objectB}. Physical relation: {rel}. Background: {bg}.\n" +
                   $"Output ONLY JSON with fields: type=inference, answer.larger ('A'|'B'), confidence (0..1).";
        }

        // ============ Tool Specifications (for action_plan) ============

        // 注：ParameterSpec.PropertySpec 是占位结构，JsonUtility 对匿名结构支持有限；
        // 这里将参数 Schema 简化为 type/object + required 字段，供后端对齐提示工程时展示用。
        public static ToolSpec[] GetToolsForDistanceCompression()
        {
            return new[]
            {
                MakeTool("camera_set_fov", "Set camera field-of-view (degrees).",
                    new [] { "fov_deg" }),
                MakeTool("head_look_at", "Look at a target by name or position.",
                    new [] { "target" }), // 简化：必选其一
                MakeTool("move_forward", "Move forward in meters.",
                    new [] { "meters" }),
                MakeTool("strafe", "Strafe left/right by meters.",
                    new [] { "meters", "direction" }),
                MakeTool("turn_yaw", "Turn yaw in degrees.",
                    new [] { "deg" }),
                MakeTool("snapshot", "Request a frame capture.",
                    new string[] { })
            };
        }

        public static ToolSpec[] GetToolsForSemanticSizeBias()
        {
            return new[]
            {
                MakeTool("focus_target", "Focus on a target by name.", new [] { "name" }),
                MakeTool("head_look_at", "Look at a target by name or position.", new [] { "target" }),
                MakeTool("snapshot", "Request a frame capture.", new string[] { })
            };
        }

        private static ToolSpec MakeTool(string name, string description, string[] required)
        {
            return new ToolSpec
            {
                name = name,
                description = description,
                parameters = new ParameterSpec
                {
                    type = "object",
                    required = required ?? Array.Empty<string>(),
                    properties = new PropertySpec() // 简化：属性结构留空，由后端更严格验证
                }
            };
        }
    }
}