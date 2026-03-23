using System;
using System.Globalization;
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
                case "depth_jnd_staircase":
                    return DepthJndStaircaseSystem();
                case "change_detection":
                    return ChangeDetectionSystem();
                case "occlusion_reasoning":
                    return OcclusionReasoningSystem();
                case "color_constancy":
                    return ColorConstancySystem();
                case "color_constancy_adjustment":
                    return ColorConstancyAdjustmentSystem();
                case "material_perception":
                    return MaterialPerceptionSystem();
                case "material_roughness":
                case "material_roughness_motion":
                case "material_roughness_static":
                    return MaterialRoughnessSystem();
                case "visual_search":
                    return VisualSearchSystem();
                case "object_counting":
                    return ObjectCountingSystem();
                case "visual_crowding":
                    return VisualCrowdingSystem();
                case "numerosity_comparison":
                    return NumerosityComparisonSystem();
                case "horizon_cue_integration":
                    return HorizonCueIntegrationSystem();
                default:
                    return GenericSystem();
            }
        }

        private static string GenericSystem()
        {
            return "You are a participant in a controlled perception experiment. " +
                   "Answer ONLY using information visible in the provided image(s). Do not use outside knowledge. " +
                   "You may receive one or more snapshots captured within the same trial from slightly different viewpoints. Use all provided snapshots jointly as evidence. Do NOT output any action_plan or tool calls. " +
                   "Output must be STRICT JSON and must match the exact schema given in the user message. " +
                   "Do not include any extra text outside the JSON. " +
                   "If the image(s) are missing, unclear, or the task cannot be completed, output valid=false and confidence=0.0 in the required JSON schema.";
        }

        private static string DistanceCompressionSystem()
        {
            return "You are a participant in a controlled VR distance perception experiment. " +
                   "Answer ONLY using information visible in the provided image(s). Do not use outside knowledge. " +
                   "You may receive one or more snapshots captured within the same trial from slightly different viewpoints. Use all provided snapshots jointly as evidence. Do NOT output any action_plan or tool calls. " +
                   "Output must be STRICT JSON and must match the exact schema provided in the user message. " +
                   "Do not include any extra text outside the JSON. " +
                   "If the image(s) are missing or unclear, output valid=false and confidence=0.0 in the required JSON schema.";
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

        private static string DepthJndStaircaseSystem()
        {
            return "You are a participant in a controlled VR relative depth discrimination experiment. " +
                   "Answer ONLY using information visible in the provided image(s). Do not use outside knowledge. " +
                   "You may receive one or more snapshots captured within the same trial from slightly different viewpoints. Use all provided snapshots jointly as evidence. Do NOT output any action_plan or tool calls. " +
                   "Output must be STRICT JSON and must match the exact schema given in the user message. " +
                   "Do not include any extra text outside the JSON. " +
                   "If the image(s) are missing, unclear, or the task cannot be completed, output valid=false and confidence=0.0 in the required JSON schema.";
        }

        private static string ChangeDetectionSystem()
        {
            return "You are a participant in a controlled VR visual change detection experiment. " +
                   "Answer ONLY using information visible in the provided image(s). Do not use outside knowledge. " +
                   "You may receive one or more snapshots captured within the same trial from slightly different viewpoints. Use all provided snapshots jointly as evidence. Do NOT output any action_plan or tool calls. " +
                   "Output must be STRICT JSON and must match the exact schema given in the user message. " +
                   "Do not include any extra text outside the JSON. " +
                   "If the image(s) are missing, unclear, or the task cannot be completed, output valid=false and confidence=0.0 in the required JSON schema.";
        }

        private static string OcclusionReasoningSystem()
        {
            return "You are a vision agent for Occlusion Reasoning & Counting. ONLY output JSON. " +
                   "Your goal is to decide whether target objects of a specified category are present in the view under partial occlusion, " +
                   "and, if present, estimate how many are visible. " +
                   "Inference format: {\"type\":\"inference\",\"taskId\":\"occlusion_reasoning\",\"trialId\":<int>," +
                   "\"answer\":{\"present\":true|false,\"count\":<int>},\"confidence\":<0..1>} " +
                   "If more information is needed, you may output {\"type\":\"action_plan\",\"actions\":[...]} with the provided tools. " +
                   "Do NOT output any extra text.";
        }

        private static string MaterialRoughnessSystem()
        {
            return "You are a participant in a controlled VR material perception experiment. " +
                   "Answer ONLY using information visible in the provided image(s). Do not use outside knowledge. " +
                   "You may receive one or more snapshots captured within the same trial from slightly different viewpoints. Use all provided snapshots jointly as evidence. Do NOT output any action_plan or tool calls. " +
                   "Output must be STRICT JSON and must match the exact schema given in the user message. " +
                   "Do not include any extra text outside the JSON. " +
                   "If the image(s) are missing, unclear, or the task cannot be completed, output valid=false and confidence=0.0 in the required JSON schema.";
        }

        private static string ColorConstancySystem()
        {
            return "You are a vision agent for Color Constancy. ONLY output JSON. " +
                   "Your goal is to infer the perceived surface color of the main target object, discounting lighting color and brightness. " +
                   "Inference format: {\"type\":\"inference\",\"taskId\":\"color_constancy\",\"trialId\":<int>," +
                   "\"answer\":{\"color_name\":\"red|green|blue|yellow|white|gray\",\"rgb\":[R,G,B]},\"confidence\":<0..1>} " +
                   "If more information is needed, you may output {\"type\":\"action_plan\",\"actions\":[...]} with the provided tools. " +
                   "Do NOT output any extra text.";
        }

        private static string ColorConstancyAdjustmentSystem()
        {
            return "You are a participant in a controlled VR color perception experiment. " +
                   "Answer ONLY using information visible in the provided image(s). Do not use outside knowledge. " +
                   "You may receive one or more snapshots captured within the same trial from slightly different viewpoints. Use all provided snapshots jointly as evidence. Do NOT output any action_plan or tool calls. " +
                   "Output must be STRICT JSON and must match the exact schema given in the user message. " +
                   "Do not include any extra text outside the JSON. " +
                   "If the image(s) are missing, unclear, or the task cannot be completed, output valid=false and confidence=0.0 in the required JSON schema.";
        }

        private static string MaterialPerceptionSystem()
        {
            return "You are a vision agent for Material Perception. ONLY output JSON. " +
                   "Your goal is to classify the dominant material of the main target object using cues such as specular highlights, reflections, and surface roughness. " +
                   "Inference format: {\"type\":\"inference\",\"taskId\":\"material_perception\",\"trialId\":<int>," +
                   "\"answer\":{\"material\":\"metal|glass|wood|fabric|sand|rock\"},\"confidence\":<0..1>} " +
                   "If more information is needed, you may output {\"type\":\"action_plan\",\"actions\":[...]} with the provided tools. " +
                   "Do NOT output any extra text.";
        }

        private static string VisualSearchSystem()
        {
            return "You are a vision agent for Visual Search in clutter. ONLY output JSON. " +
                   "Your goal is to decide whether the target object is present among distractors. " +
                   "Inference format: {\"type\":\"inference\",\"taskId\":\"visual_search\",\"trialId\":<int>," +
                   "\"answer\":{\"found\":true|false,\"target\":\"<name>\"},\"confidence\":<0..1>} " +
                   "If more information is needed, you may output {\"type\":\"action_plan\",\"actions\":[...]} with the provided tools. " +
                   "Never output extra text.";
        }

        private static string ObjectCountingSystem()
        {
            return "You are a vision agent for Object Counting. ONLY output JSON. " +
                   "Your goal is to estimate how many target objects of the specified category are visible in the scene. " +
                   "Inference format: {\"type\":\"inference\",\"taskId\":\"object_counting\",\"trialId\":<int>," +
                   "\"answer\":{\"count\":<int>},\"confidence\":<0..1>} " +
                   "If more information is needed, you may output {\"type\":\"action_plan\",\"actions\":[...]} with the provided tools. " +
                   "Do NOT output any extra text.";
        }

        private static string VisualCrowdingSystem()
        {
            return "You are a participant in a controlled VR peripheral vision experiment. " +
                   "Answer ONLY using information visible in the provided image(s). Do not use outside knowledge. " +
                   "You may receive one or more snapshots captured within the same trial from slightly different viewpoints. Use all provided snapshots jointly as evidence. Do NOT output any action_plan or tool calls. " +
                   "Output must be STRICT JSON and must match the exact schema given in the user message. " +
                   "Do not include any extra text outside the JSON. " +
                   "If the image(s) are missing, unclear, or the task cannot be completed, output valid=false and confidence=0.0 in the required JSON schema.";
        }

        private static string NumerosityComparisonSystem()
        {
            return "You are a participant in a controlled VR numerosity perception experiment. " +
                   "Answer ONLY using information visible in the provided image(s). Do not use outside knowledge. " +
                   "You may receive one or more snapshots captured within the same trial from slightly different viewpoints. Use all provided snapshots jointly as evidence. Do NOT output any action_plan or tool calls. " +
                   "Output must be STRICT JSON and must match the exact schema given in the user message. " +
                   "Do not include any extra text outside the JSON. " +
                   "If the image(s) are missing, unclear, or the task cannot be completed, output valid=false and confidence=0.0 in the required JSON schema.";
        }

        private static string HorizonCueIntegrationSystem()
        {
            return "You are a participant in a controlled VR horizon cue integration experiment. " +
                   "Answer ONLY using information visible in the provided image(s). Do not use outside knowledge. " +
                   "You may receive one or more snapshots captured within the same trial from slightly different viewpoints. Use all provided snapshots jointly as evidence. Do NOT output any action_plan or tool calls. " +
                   "Output must be STRICT JSON and must match the exact schema given in the user message. " +
                   "Do not include any extra text outside the JSON. " +
                   "If the image(s) are missing, unclear, or the task cannot be completed, output valid=false and confidence=0.0 in the required JSON schema.";
        }

        // ============ Task Prompts ============

        public static string BuildDistanceCompressionPrompt(string targetKind, float fovDeg, string environment, int trialId)
        {
            var id = trialId.ToString(CultureInfo.InvariantCulture);
            return
                $"Trial metadata: {{\"task\":\"distance_compression\",\"trial_id\":\"{id}\",\"phase\":\"main\"}}\n" +
                "Estimate the distance from the camera to the ball in meters.\n" +
                $"Output STRICT JSON exactly in this schema: {{\"task\":\"distance_compression\",\"trial_id\":\"{id}\",\"response\":{{\"distance_m\": <number>}},\"confidence\": <0.0-1.0>,\"valid\": true}}";
        }

        public static string BuildDistanceCompressionCalibrationPrompt(string targetKind, float fovDeg, string environment, float trueDistanceM, int trialId)
        {
            var distText = trueDistanceM.ToString("0.###", CultureInfo.InvariantCulture);
            var id = trialId.ToString(CultureInfo.InvariantCulture);

            return
                $"Trial metadata: {{\"task\":\"distance_calibration\",\"trial_id\":\"{id}\",\"phase\":\"calibration\"}}\n" +
                $"This image shows a ball at a distance of {distText} meters from the camera.\n" +
                "Study it to calibrate your sense of distance in this experiment.\n" +
                $"Output STRICT JSON exactly in this schema: {{\"task\":\"distance_calibration\",\"trial_id\":\"{id}\",\"response\":{{\"acknowledged_distance_m\": {distText}}},\"confidence\": <0.0-1.0>,\"valid\": true}}";
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

        public static string BuildDepthJndStaircasePrompt(string background, float fovDeg, int trialId)
        {
            var id = trialId.ToString(CultureInfo.InvariantCulture);
            return
                $"Trial metadata: {{\"task\":\"depth_jnd_staircase\",\"trial_id\":\"{id}\",\"phase\":\"main\"}}\n" +
                "Two identical objects are shown in the same frame: A on the left and B on the right. Choose which object is closer to the camera.\n" +
                "If multiple snapshots are provided, treat them as complementary views of the same trial and return one final judgment.\n" +
                $"Output STRICT JSON exactly in this schema: {{\"task\":\"depth_jnd_staircase\",\"trial_id\":\"{id}\",\"response\":{{\"closer\":\"<A_or_B>\"}},\"confidence\":<0.0-1.0>,\"valid\":true}}. " +
                "Set response.closer to exactly \"A\" or \"B\". Do not output \"|\" or placeholder text.";
        }

        public static string BuildVisualCrowdingPrompt(float eccentricityDeg, float spacingDeg, string targetLetter, string[] flankers, int trialId)
        {
            var id = trialId.ToString(CultureInfo.InvariantCulture);
            return
                $"Trial metadata: {{\"task\":\"visual_crowding\",\"trial_id\":\"{id}\",\"phase\":\"main\"}}\n" +
                "A central fixation mark is present. Keep attention on the fixation point. A horizontal 5-letter string appears in the right periphery; the middle letter is the target. Identify the target letter.\n" +
                $"Output STRICT JSON exactly in this schema: {{\"task\":\"visual_crowding\",\"trial_id\":\"{id}\",\"response\":{{\"letter\":\"<letter>\"}},\"confidence\":<0.0-1.0>,\"valid\":true}}";
        }

        public static string BuildChangeDetectionPrompt(string background, float fovDeg, int trialId)
        {
            var id = trialId.ToString(CultureInfo.InvariantCulture);
            return
                $"Trial metadata: {{\"task\":\"change_detection\",\"trial_id\":\"{id}\",\"phase\":\"main\"}}\n" +
                "You are given two sequential views from the same viewpoint within one trial. The first image is scene A (before), then a brief mask occurred, then the second image is scene B (after). Decide whether any change occurred from A to B. If changed, classify the change as one of: appearance, disappearance, movement, replacement. If no change, report category as none.\n" +
                "If two images are provided, interpret them strictly in temporal order: first=before, second=after. Aggregate evidence across the provided images and return one final judgment.\n" +
                $"Output STRICT JSON exactly in this schema: {{\"task\":\"change_detection\",\"trial_id\":\"{id}\",\"response\":{{\"changed\":<bool>,\"category\":\"<category>\"}},\"confidence\":<0.0-1.0>,\"valid\":true}}. " +
                "Set response.changed to true or false. Set response.category to exactly one of: \"appearance\", \"disappearance\", \"movement\", \"replacement\", \"none\". " +
                "Do not output \"|\" or placeholder text.";
        }

        public static string BuildOcclusionReasoningPrompt(string targetCategory, string occluderType, float occlusionRatio, string background, float fovDeg)
        {
            var bg = string.IsNullOrEmpty(background) ? "none" : background;
            var rawOcc = string.IsNullOrEmpty(occluderType) ? "wall" : occluderType;
            var occLower = rawOcc.ToLowerInvariant();
            var occLabel =
                occLower.Contains("plant") ? "plant" :
                occLower.Contains("pedestrian") ? "pedestrian" :
                occLower.Contains("wall") ? "wall" :
                occLower.Contains("fence") ? "fence" :
                rawOcc;

            var clampedRatio = Mathf.Clamp01(occlusionRatio);
            var fov = fovDeg > 0 ? fovDeg : 60f;
            var target = string.IsNullOrEmpty(targetCategory) ? "cube" : targetCategory;

            var sb = new StringBuilder();
            sb.AppendLine("Task: Decide whether the target objects are present in the scene and estimate their visible count under partial occlusion.");
            sb.AppendLine($"Target category: {target}.");
            sb.AppendLine($"Scene conditions: background={bg}, occluder_type={occLabel}, occlusion_ratio≈{clampedRatio:0.00}, FOV={fov} deg.");
            sb.AppendLine("If no target of the requested category is visible, set present=false and count=0.");
            sb.Append("Output ONLY JSON with fields: type=inference, answer.present (true/false), answer.count (integer ≥0), confidence (0..1).");
            return sb.ToString();
        }

        public static string BuildVisualSearchPrompt(string targetCategory, string distractorCategory, int setSize, string similarityLevel, string background, float fovDeg)
        {
            var target = string.IsNullOrEmpty(targetCategory) ? "red_cup" : targetCategory;
            var distractor = string.IsNullOrEmpty(distractorCategory) ? "blue_cup" : distractorCategory;
            var bg = string.IsNullOrEmpty(background) ? "none" : background;
            var sim = string.IsNullOrEmpty(similarityLevel) ? "easy" : similarityLevel;
            var fov = fovDeg > 0 ? fovDeg : 60f;

            var sb = new StringBuilder();
            sb.AppendLine("Task: Decide whether the specified target object is present among distractors.");
            sb.AppendLine($"Target category: {target}. Distractors: {distractor}. Set size: {setSize}. Similarity level: {sim}.");
            sb.AppendLine($"Scene conditions: background={bg}, FOV={fov} deg.");
            sb.Append("Output ONLY JSON with fields: type=inference, answer.found (true/false), optional answer.target (string), confidence (0..1).");
            return sb.ToString();
        }

        public static string BuildObjectCountingPrompt(string targetCategory, string layoutPattern, float occlusionRatio, string background, float fovDeg)
        {
            var target = string.IsNullOrEmpty(targetCategory) ? "count_ball" : targetCategory;
            var layout = string.IsNullOrEmpty(layoutPattern) ? "random" : layoutPattern;
            var bg = string.IsNullOrEmpty(background) ? "none" : background;
            var fov = fovDeg > 0 ? fovDeg : 60f;
            var occ = Mathf.Clamp01(occlusionRatio);

            var sb = new StringBuilder();
            sb.AppendLine("Task: Estimate how many target objects are visible in the current view.");
            sb.AppendLine($"Target category: {target}. Expected count range covers small to dense sets.");
            sb.AppendLine($"Scene conditions: layout={layout}, background={bg}, occlusion_ratio≈{occ:0.00}, FOV={fov} deg.");
            sb.AppendLine("If unsure, provide your best estimate in an integer count.");
            sb.Append("Output ONLY JSON with fields: type=inference, answer.count (integer ≥0), confidence (0..1).");
            return sb.ToString();
        }

        public static string BuildColorConstancyPrompt(string targetKind, string background, string lighting, string material, bool hasShadow, float fovDeg)
        {
            var kind = string.IsNullOrEmpty(targetKind) ? "color_patch" : targetKind;
            var bg = string.IsNullOrEmpty(background) ? "none" : background;
            var light = string.IsNullOrEmpty(lighting) ? "bright" : lighting;
            var mat = string.IsNullOrEmpty(material) ? "matte" : material;
            var shadow = hasShadow ? "with noticeable shadows" : "without strong shadows";
            var fov = fovDeg > 0 ? fovDeg : 60f;

            var sb = new StringBuilder();
            sb.AppendLine("Task: Estimate the perceived surface color of the main target patch/object while discounting lighting color and brightness.");
            sb.AppendLine($"Target kind: {kind}.");
            sb.AppendLine($"Scene conditions: background={bg}, lighting={light}, material={mat}, shadows={shadow}, FOV={fov} deg.");
            sb.AppendLine("Focus on the inherent surface color (as a human would describe the object under neutral white light).");
            sb.Append("Output ONLY JSON with fields: type=inference, answer.color_name (string, e.g. 'red','green','blue','yellow','white','gray'), ");
            sb.Append("answer.rgb (array [R,G,B] with integer values 0-255), confidence (0..1).");
            return sb.ToString();
        }

        public static string BuildColorConstancyAdjustmentPrompt(string phase, string background, string lighting, float fovDeg, int trialId)
        {
            var id = trialId.ToString(CultureInfo.InvariantCulture);
            return
                $"Trial metadata: {{\"task\":\"color_constancy_adjustment\",\"trial_id\":\"{id}\",\"phase\":\"main\"}}\n" +
                "Choose the labeled ball that appears most neutral gray under the current lighting.\n" +
                $"Output STRICT JSON exactly in this schema: {{\"task\":\"color_constancy_adjustment\",\"trial_id\":\"{id}\",\"response\":{{\"choice\":\"<letter>\"}},\"confidence\":<0.0-1.0>,\"valid\":true}}";
        }

        public static string BuildNumerosityComparisonPrompt(float fovDeg, int trialId)
        {
            var id = trialId.ToString(CultureInfo.InvariantCulture);
            return
                $"Trial metadata: {{\"task\":\"numerosity_comparison\",\"trial_id\":\"{id}\",\"phase\":\"main\"}}\n" +
                "The scene is split into left and right halves, each containing a set of similar items. Decide which side has more items at a glance; do not count one-by-one.\n" +
                $"Output STRICT JSON exactly in this schema: {{\"task\":\"numerosity_comparison\",\"trial_id\":\"{id}\",\"response\":{{\"more_side\":\"<left_or_right>\"}},\"confidence\":<0.0-1.0>,\"valid\":true}}. " +
                "Set response.more_side to exactly \"left\" or \"right\". Do not output \"|\" or placeholder text.";
        }

        public static string BuildMaterialPerceptionPrompt(string targetKind, string background, string lighting, float yawDeg)
        {
            var kind = string.IsNullOrEmpty(targetKind) ? "object" : targetKind;
            var bg = string.IsNullOrEmpty(background) ? "none" : background;
            var light = string.IsNullOrEmpty(lighting) ? "bright" : lighting;
            var yaw = Math.Abs(yawDeg) > 0.01f ? yawDeg : 0f;

            var sb = new StringBuilder();
            sb.AppendLine("Task: Identify the dominant material of the target object.");
            sb.AppendLine($"Target shape: {kind}. Scene background: {bg}. Lighting preset: {light}.");
            sb.AppendLine($"Object is oriented at yaw ≈ {yaw:0} degrees to expose specular cues.");
            sb.Append("Output ONLY JSON with fields: type=inference, answer.material ('metal'|'glass'|'wood'|'fabric'|'sand'|'rock'), confidence (0..1).");
            return sb.ToString();
        }

        public static string BuildMaterialRoughnessPrompt(string environment, bool requireHeadMotion, float fovDeg, int trialId)
        {
            var id = trialId.ToString(CultureInfo.InvariantCulture);
            return
                $"Trial metadata: {{\"task\":\"material_roughness\",\"trial_id\":\"{id}\",\"phase\":\"main\"}}\n" +
                "Estimate the surface roughness of the metallic sphere. Roughness: 0 = perfectly smooth mirror-like; 1 = completely rough matte.\n" +
                $"Output STRICT JSON exactly in this schema: {{\"task\":\"material_roughness\",\"trial_id\":\"{id}\",\"response\":{{\"roughness\":<0.0-1.0>}},\"confidence\":<0.0-1.0>,\"valid\":true}}";
        }

        public static string BuildHorizonCueIntegrationPrompt(int trialId)
        {
            var id = trialId.ToString(CultureInfo.InvariantCulture);
            return
                $"Trial metadata: {{\"task\":\"horizon_cue_integration\",\"trial_id\":\"{id}\",\"phase\":\"main\"}}\n" +
                "The red sphere is directly in front of the camera at the same height. Estimate its distance in meters.\n" +
                $"Output STRICT JSON exactly in this schema: {{\"task\":\"horizon_cue_integration\",\"trial_id\":\"{id}\",\"response\":{{\"distance_m\":<number>}},\"confidence\":<0.0-1.0>,\"valid\":true}}";
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

        public static ToolSpec[] GetToolsForOcclusionReasoning()
        {
            return new[]
            {
                MakeTool("head_look_at", "Look at a target by name or position.", new [] { "target" }),
                MakeTool("turn_yaw", "Turn yaw in degrees.", new [] { "deg" }),
                MakeTool("snapshot", "Request a frame capture.", Array.Empty<string>()),
                MakeTool("focus_target", "Focus on a target by name.", new [] { "name" })
            };
        }

        public static ToolSpec[] GetToolsForColorConstancy()
        {
            return new[]
            {
                MakeTool("set_lighting", "Adjust global lighting preset for the scene.", new [] { "preset" }),
                MakeTool("camera_set_fov", "Set camera field-of-view (degrees).", new [] { "fov_deg" }),
                MakeTool("head_look_at", "Look at a target by name or position.", new [] { "target" }),
                MakeTool("snapshot", "Request a frame capture.", Array.Empty<string>())
            };
        }

        public static ToolSpec[] GetToolsForColorConstancyAdjustment()
        {
            // 默认不启用工具，避免引入额外变量
            return Array.Empty<ToolSpec>();
        }

        public static ToolSpec[] GetToolsForMaterialPerception()
        {
            return new[]
            {
                MakeTool("set_lighting", "Adjust global lighting preset for the scene.", new [] { "preset" }),
                MakeTool("turn_yaw", "Turn yaw in degrees to change specular highlights.", new [] { "deg" }),
                MakeTool("head_look_at", "Look at a target by name or position.", new [] { "target" }),
                MakeTool("snapshot", "Request a frame capture.", Array.Empty<string>())
            };
        }

        public static ToolSpec[] GetToolsForMaterialRoughness()
        {
            // 该任务默认不提供工具，以避免模型走 action_plan 分支造成额外不可控变量。
            return Array.Empty<ToolSpec>();
        }

        public static ToolSpec[] GetToolsForVisualSearch()
        {
            return new[]
            {
                MakeTool("snapshot", "Request a frame capture.", Array.Empty<string>()),
                MakeTool("head_look_at", "Look at a target by name or position.", new [] { "target" }),
                MakeTool("turn_yaw", "Turn yaw in degrees to sweep the scene.", new [] { "deg" }),
                MakeTool("focus_target", "Focus on a suspected target by name.", new [] { "name" })
            };
        }

        public static ToolSpec[] GetToolsForObjectCounting()
        {
            return new[]
            {
                MakeTool("snapshot", "Request an additional frame capture.", Array.Empty<string>()),
                MakeTool("turn_yaw", "Turn yaw in degrees to scan wider area.", new [] { "deg" }),
                MakeTool("head_look_at", "Look at a region or target by name/position.", new [] { "target" }),
                MakeTool("camera_set_fov", "Adjust camera FOV (degrees) to encompass large sets.", new [] { "fov_deg" })
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
