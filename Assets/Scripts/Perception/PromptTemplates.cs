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
                case "relative_depth_order":
                    return RelativeDepthOrderSystem();
                case "change_detection":
                    return ChangeDetectionSystem();
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

        private static string RelativeDepthOrderSystem()
        {
            return "You are a vision agent for Relative Depth Ordering. ONLY output JSON. " +
                   "Your goal is to decide which object (A or B) is closer to the camera in the image. " +
                   "Inference format: {\"type\":\"inference\",\"taskId\":\"relative_depth_order\",\"trialId\":<int>," +
                   "\"answer\":{\"closer\":\"A\"|\"B\"},\"confidence\":<0..1>} " +
                   "If more information is needed, you may output {\"type\":\"action_plan\",\"actions\":[...]} with provided tools. " +
                   "Do NOT output any extra text.";
        }

        private static string ChangeDetectionSystem()
        {
            return "You are a vision agent for Change Detection. ONLY output JSON. " +
                   "You will see two versions of a scene in a single image: A (before) on the left and B (after) on the right. " +
                   "Your goal is to decide whether the scene has changed between A and B, and if so, what type of change. " +
                   "Inference format: {\"type\":\"inference\",\"taskId\":\"change_detection\",\"trialId\":<int>," +
                   "\"answer\":{\"changed\":true|false,\"category\":\"none\"|\"appearance\"|\"disappearance\"|\"movement\"|\"replacement\"},\"confidence\":<0..1>} " +
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

        public static string BuildRelativeDepthOrderPrompt(string background, string sizeCondition, bool occlusion, float fovDeg)
        {
            var bg = string.IsNullOrEmpty(background) ? "none" : background;
            var size = string.IsNullOrEmpty(sizeCondition) ? "equal" : sizeCondition;
            var occ = occlusion ? "with occluders" : "without occluders";
            var fov = fovDeg > 0 ? fovDeg : 60f;

            var sb = new StringBuilder();
            sb.AppendLine("Task: Decide which object is closer to the camera (A or B).");
            sb.AppendLine($"Scene conditions: background={bg}, size_condition={size}, occlusion={occ}, FOV={fov} deg.");
            sb.AppendLine("Object A is on the left side of the view; object B is on the right side.");
            sb.Append("Output ONLY JSON with fields: ");
            sb.Append("type=inference, answer.closer ('A'|'B'), confidence (0..1).");
            return sb.ToString();
        }

        public static string BuildChangeDetectionPrompt(string background, float fovDeg)
        {
            var bg = string.IsNullOrEmpty(background) ? "none" : background;
            var fov = fovDeg > 0 ? fovDeg : 60f;

            var sb = new StringBuilder();
            sb.AppendLine("Task: Decide whether the scene has changed between A and B.");
            sb.AppendLine("In the image, the left side shows scene A (before) and the right side shows scene B (after).");
            sb.AppendLine($"Conditions: background={bg}, FOV={fov} deg.");
            sb.AppendLine("Possible change categories: 'none', 'appearance', 'disappearance', 'movement', 'replacement'.");
            sb.Append("Output ONLY JSON with fields: type=inference, answer.changed (true/false), ");
            sb.Append("answer.category ('none'|'appearance'|'disappearance'|'movement'|'replacement'), confidence (0..1).");
            return sb.ToString();
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

        public static ToolSpec[] GetToolsForRelativeDepthOrder()
        {
            return new[]
            {
                MakeTool("snapshot", "Request a frame capture.", new string[] { }),
                MakeTool("strafe", "Strafe left/right by meters.", new [] { "meters", "direction" }),
                MakeTool("turn_yaw", "Turn yaw in degrees.", new [] { "deg" }),
                MakeTool("head_look_at", "Look at a target by name or position.", new [] { "target" }),
                MakeTool("camera_set_fov", "Set camera field-of-view (degrees).", new [] { "fov_deg" })
            };
        }

        public static ToolSpec[] GetToolsForChangeDetection()
        {
            return new[]
            {
                MakeTool("snapshot", "Request a frame capture.", new string[] { }),
                MakeTool("head_look_at", "Look at a target by name or position.", new [] { "target" }),
                MakeTool("focus_target", "Focus on a specific region or object.", new [] { "name" })
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
