using UnityEngine;

namespace ArchUnityDemo
{
    /// <summary>
    /// 教程项目入口：挂载到场景主摄像机或空 GameObject 上即可。
    /// 它会自动创建 DebugHUD 和 DemoRunner。
    /// </summary>
    [DefaultExecutionOrder(-1000)]
    public class GameMain : MonoBehaviour
    {
        private void Awake()
        {
            // 确保 DebugHUD 存在
            if (FindObjectOfType<DebugHUD>() == null)
            {
                var hudObj = new GameObject("[DebugHUD]");
                hudObj.AddComponent<DebugHUD>();
            }

            // 确保 DemoRunner 存在
            if (FindObjectOfType<DemoRunner>() == null)
            {
                var runnerObj = new GameObject("[DemoRunner]");
                runnerObj.AddComponent<DemoRunner>();
            }

            // 自身已完成使命，可销毁
            // 但保留以便在 Inspector 中看到状态
        }

        private void OnGUI()
        {
            // 顶部标题
            var rect = new Rect(Screen.width / 2f - 200f, 5f, 400f, 24f);
            GUI.Label(rect, "<size=14><b>Unity Arch ECS 框架新手教程 - 演示项目</b></size>",
                new GUIStyle(GUI.skin.label)
                {
                    richText = true,
                    alignment = TextAnchor.MiddleCenter,
                    normal = { textColor = Color.white }
                });
        }
    }
}
