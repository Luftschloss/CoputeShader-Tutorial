using UnityEngine;

public static class GpuDrivenShowcaseBootstrap
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void CreateRuntimeLayer()
    {
        GpuDrivenShowcaseController controller = Object.FindObjectOfType<GpuDrivenShowcaseController>(true);
        if (controller == null)
        {
            GameObject controllerObject = new GameObject("GPU Driven Showcase");
            controller = controllerObject.AddComponent<GpuDrivenShowcaseController>();
            controllerObject.AddComponent<GpuDrivenShowcaseRuntimePanel>();
        }
        else if (controller.GetComponent<GpuDrivenShowcaseRuntimePanel>() == null)
        {
            controller.gameObject.AddComponent<GpuDrivenShowcaseRuntimePanel>();
        }

        Camera camera = Camera.main;
        if (camera != null && camera.GetComponent<GpuDrivenFreeCamera>() == null)
        {
            camera.gameObject.AddComponent<GpuDrivenFreeCamera>();
        }

        controller.RefreshModules();
    }
}
