using UnityEngine;

namespace ArchUnityDemo
{
    /// <summary>
    /// 可视化实体用的简单 GameObject 工厂。
    /// 用于将 ECS 实体与 GameObject 关联（仅用于演示，生产环境请用 Arch.Unity）。
    /// </summary>
    public static class VisualFactory
    {
        /// <summary>创建一个代表实体的彩色立方体</summary>
        public static GameObject CreateCube(Vector3 position, Color color, string name = "Entity")
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.position = position;
            go.transform.localScale = Vector3.one * 0.8f;
            var renderer = go.GetComponent<MeshRenderer>();
            if (renderer != null)
            {
                var mat = new Material(Shader.Find("Standard") ?? Shader.Find("Universal Render Pipeline/Lit"));
                mat.color = color;
                renderer.sharedMaterial = mat;
            }
            return go;
        }

        /// <summary>创建一个文本标签</summary>
        public static GameObject CreateLabel(Vector3 position, string text)
        {
            var go = new GameObject($"Label:{text}");
            go.transform.position = position;
            var tm = go.AddComponent<TextMesh>();
            tm.text = text;
            tm.characterSize = 0.1f;
            tm.fontSize = 32;
            tm.anchor = TextAnchor.MiddleCenter;
            return go;
        }
    }
}
