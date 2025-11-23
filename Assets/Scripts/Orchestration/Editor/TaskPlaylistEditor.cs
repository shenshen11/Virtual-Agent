#if UNITY_EDITOR
using System;
using System.Linq;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using VRPerception.Orchestration;
using VRPerception.Tasks;

namespace VRPerception.Orchestration.Editor
{
    [CustomEditor(typeof(TaskPlaylist))]
    public sealed class TaskPlaylistEditor : UnityEditor.Editor
    {
        private static readonly string[] BuiltInTaskIds =
        {
            "distance_compression",
            "semantic_size_bias"
        };

        private SerializedProperty playlistIdProp;
        private SerializedProperty displayNameProp;
        private SerializedProperty descriptionProp;
        private SerializedProperty entriesProp;
        private SerializedProperty defaultParticipantIdProp;
        private SerializedProperty defaultRestSecondsProp;
        private SerializedProperty defaultRandomSeedProp;

        private ReorderableList entriesList;
        private bool showMetaFoldout = true;

        private TaskPlaylist PlaylistTarget => (TaskPlaylist)target;

        private void OnEnable()
        {
            playlistIdProp = serializedObject.FindProperty("playlistId");
            displayNameProp = serializedObject.FindProperty("displayName");
            descriptionProp = serializedObject.FindProperty("description");
            entriesProp = serializedObject.FindProperty("entries");
            defaultParticipantIdProp = serializedObject.FindProperty("defaultParticipantId");
            defaultRestSecondsProp = serializedObject.FindProperty("defaultRestSeconds");
            defaultRandomSeedProp = serializedObject.FindProperty("defaultRandomSeed");

            entriesList = new ReorderableList(serializedObject, entriesProp, true, true, true, true);
            entriesList.drawHeaderCallback = DrawEntriesHeader;
            entriesList.drawElementCallback = DrawEntryElement;
            entriesList.elementHeightCallback = GetEntryHeight;
            entriesList.onAddDropdownCallback = HandleAddDropdown;
            entriesList.onRemoveCallback = HandleRemove;
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            DrawMetadataSection();
            EditorGUILayout.Space(8f);

            EditorGUILayout.LabelField("Playlist Defaults", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(defaultParticipantIdProp);
            EditorGUILayout.PropertyField(defaultRestSecondsProp, new GUIContent("Default Rest Seconds"));
            EditorGUILayout.PropertyField(defaultRandomSeedProp, new GUIContent("Default Random Seed"));

            EditorGUILayout.Space(12f);
            entriesList.DoLayoutList();

            serializedObject.ApplyModifiedProperties();
        }

        private void DrawMetadataSection()
        {
            showMetaFoldout = EditorGUILayout.BeginFoldoutHeaderGroup(showMetaFoldout, "Metadata");
            if (showMetaFoldout)
            {
                using (new EditorGUI.DisabledScope(true))
                {
                    EditorGUILayout.PropertyField(playlistIdProp);
                }

                EditorGUILayout.PropertyField(displayNameProp);
                EditorGUILayout.PropertyField(descriptionProp, true);

                EditorGUILayout.HelpBox(
                    "Playlist 将由 Orchestrator 在运行时载入。可通过“Create > VR Perception > Task Playlist”创建资产，并放置到 Resources/Playlists 目录以便运行时加载。",
                    MessageType.Info);
            }
            EditorGUILayout.EndFoldoutHeaderGroup();
        }

        private void DrawEntriesHeader(Rect rect)
        {
            GUI.Label(rect, $"Entries ({entriesProp.arraySize})");
        }

        private void HandleAddDropdown(Rect buttonRect, ReorderableList list)
        {
            var menu = new GenericMenu();
            var registryIds = GetRegistryTaskIds();

            if (registryIds.Length == 0)
            {
                menu.AddDisabledItem(new GUIContent("No registered tasks found"));
            }
            else
            {
                foreach (var id in registryIds)
                {
                    menu.AddItem(new GUIContent($"From Registry/{id}"), false, () => AddEntry(entry =>
                    {
                        entry.FindPropertyRelative("taskId").stringValue = id;
                        entry.FindPropertyRelative("legacyMode").enumValueIndex = (int)TaskMode.DistanceCompression;
                    }));
                }
            }

            foreach (TaskMode mode in Enum.GetValues(typeof(TaskMode)))
            {
                var label = $"Legacy Mode/{mode}";
                menu.AddItem(new GUIContent(label), false, () => AddEntry(entry =>
                {
                    entry.FindPropertyRelative("taskId").stringValue = string.Empty;
                    entry.FindPropertyRelative("legacyMode").enumValueIndex = (int)mode;
                }));
            }

            menu.AddSeparator(string.Empty);
            menu.AddItem(new GUIContent("Custom Entry"), false, () => AddEntry(entry =>
            {
                entry.FindPropertyRelative("taskId").stringValue = string.Empty;
                entry.FindPropertyRelative("legacyMode").enumValueIndex = (int)TaskMode.DistanceCompression;
            }));

            menu.DropDown(buttonRect);
        }

        private void HandleRemove(ReorderableList list)
        {
            if (list.index >= 0 && list.index < entriesProp.arraySize)
            {
                entriesProp.DeleteArrayElementAtIndex(list.index);
                serializedObject.ApplyModifiedProperties();
            }
        }

        private void AddEntry(Action<SerializedProperty> initializer)
        {
            serializedObject.Update();

            var index = entriesProp.arraySize;
            entriesProp.arraySize++;
            var entry = entriesProp.GetArrayElementAtIndex(index);
            ResetEntry(entry);
            initializer?.Invoke(entry);

            serializedObject.ApplyModifiedProperties();
            entriesList.index = index;
        }

        private static void ResetEntry(SerializedProperty entry)
        {
            entry.FindPropertyRelative("taskId").stringValue = string.Empty;
            entry.FindPropertyRelative("legacyMode").enumValueIndex = (int)TaskMode.DistanceCompression;
            entry.FindPropertyRelative("displayName").stringValue = string.Empty;
            entry.FindPropertyRelative("description").stringValue = string.Empty;
            entry.FindPropertyRelative("subjectMode").enumValueIndex = (int)SubjectMode.MLLM;
            entry.FindPropertyRelative("randomSeed").intValue = 0;
            entry.FindPropertyRelative("maxTrials").intValue = 0;
            entry.FindPropertyRelative("enableActionPlanLoop").boolValue = true;
            entry.FindPropertyRelative("actionPlanLoopTimeoutMs").intValue = 20000;
            entry.FindPropertyRelative("requireHumanInput").boolValue = false;
            entry.FindPropertyRelative("scenePreset").stringValue = string.Empty;
            entry.FindPropertyRelative("preTaskMessage").stringValue = string.Empty;
            entry.FindPropertyRelative("postTaskMessage").stringValue = string.Empty;
            entry.FindPropertyRelative("restSeconds").floatValue = -1f;
            entry.FindPropertyRelative("operatorNotes").stringValue = string.Empty;
        }

        private void DrawEntryElement(Rect rect, int index, bool isActive, bool isFocused)
        {
            if (index < 0 || index >= entriesProp.arraySize)
            {
                return;
            }

            var element = entriesProp.GetArrayElementAtIndex(index);
            const float padding = 4f;

            rect.y += padding;
            rect.height -= padding * 2f;

            EditorGUI.PropertyField(rect, element, GUIContent.none, true);
        }

        private float GetEntryHeight(int index)
        {
            if (index < 0 || index >= entriesProp.arraySize)
            {
                return EditorGUIUtility.singleLineHeight;
            }

            var element = entriesProp.GetArrayElementAtIndex(index);
            var height = EditorGUI.GetPropertyHeight(element, GUIContent.none, true);
            return height + EditorGUIUtility.standardVerticalSpacing * 2f;
        }

        private static string[] GetRegistryTaskIds()
        {
            try
            {
                return TaskRegistry.Instance.GetRegisteredTaskIds()
                    .Concat(BuiltInTaskIds)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"TaskPlaylistEditor: Failed to read TaskRegistry. {ex.Message}");
                return BuiltInTaskIds;
            }
        }
    }
}
#endif