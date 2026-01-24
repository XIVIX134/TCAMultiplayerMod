using UnityEngine;

namespace TCAMultiplayer.Networking
{
    /// <summary>
    /// Makes a sprite always face the camera
    /// </summary>
    public class BillboardSprite : MonoBehaviour
    {
        void LateUpdate()
        {
            var cam = Camera.main;
            if (cam != null)
            {
                transform.LookAt(cam.transform);
                transform.Rotate(0, 180, 0);
            }
        }
    }

    /// <summary>
    /// Custom rendering component that draws using GL directly
    /// This bypasses the normal rendering pipeline and should always be visible
    /// </summary>
    public class RemoteMarkerRenderer : MonoBehaviour
    {
        private Material _glMaterial;
        private bool _initialized = false;

        void Start()
        {
            CreateMaterial();
        }

        void CreateMaterial()
        {
            if (_glMaterial != null) return;
            
            // Unity's built-in shader for GL drawing
            var shader = Shader.Find("Hidden/Internal-Colored");
            if (shader == null)
            {
                // Fallback
                shader = Shader.Find("Sprites/Default");
            }
            
            if (shader != null)
            {
                _glMaterial = new Material(shader);
                _glMaterial.hideFlags = HideFlags.HideAndDontSave;
                _glMaterial.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                _glMaterial.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                _glMaterial.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
                _glMaterial.SetInt("_ZWrite", 0);
                _glMaterial.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
                _initialized = true;
                Plugin.Log.LogInfo("[RemoteMarkerRenderer] GL Material created");
            }
            else
            {
                Plugin.Log.LogWarning("[RemoteMarkerRenderer] Could not find shader for GL drawing");
            }
        }

        void OnRenderObject()
        {
            if (!_initialized)
            {
                CreateMaterial();
                if (!_initialized) return;
            }

            DrawMarker();
        }

        void DrawMarker()
        {
            if (_glMaterial == null) return;

            _glMaterial.SetPass(0);

            GL.PushMatrix();
            GL.MultMatrix(transform.localToWorldMatrix);

            // Draw a 3D cross/star pattern
            float size = 30f;
            
            GL.Begin(GL.LINES);
            
            // Red - X axis
            GL.Color(Color.red);
            GL.Vertex3(-size, 0, 0);
            GL.Vertex3(size, 0, 0);
            
            // Green - Y axis  
            GL.Color(Color.green);
            GL.Vertex3(0, -size, 0);
            GL.Vertex3(0, size, 0);
            
            // Blue - Z axis
            GL.Color(Color.blue);
            GL.Vertex3(0, 0, -size);
            GL.Vertex3(0, 0, size);
            
            // Diagonal lines for visibility
            GL.Color(Color.yellow);
            GL.Vertex3(-size, -size, 0);
            GL.Vertex3(size, size, 0);
            GL.Vertex3(-size, size, 0);
            GL.Vertex3(size, -size, 0);
            
            GL.End();

            // Draw a diamond/square
            GL.Begin(GL.LINE_STRIP);
            GL.Color(Color.magenta);
            GL.Vertex3(0, size, 0);
            GL.Vertex3(size, 0, 0);
            GL.Vertex3(0, -size, 0);
            GL.Vertex3(-size, 0, 0);
            GL.Vertex3(0, size, 0);
            GL.End();

            // Draw filled triangles for more visibility
            GL.Begin(GL.TRIANGLES);
            GL.Color(new Color(1f, 0f, 0f, 0.5f));
            float s = size * 0.5f;
            // Triangle pointing up
            GL.Vertex3(0, s, 0);
            GL.Vertex3(-s, -s, 0);
            GL.Vertex3(s, -s, 0);
            GL.End();

            GL.PopMatrix();
        }

        void OnDestroy()
        {
            if (_glMaterial != null)
            {
                Destroy(_glMaterial);
            }
        }
    }

    /// <summary>
    /// Alternative marker that uses OnGUI to draw screen-space indicators
    /// This is guaranteed to be visible as it bypasses 3D rendering entirely
    /// </summary>
    public class ScreenSpaceMarker : MonoBehaviour
    {
        private static Texture2D _markerTexture;

        void Start()
        {
            if (_markerTexture == null)
            {
                _markerTexture = new Texture2D(16, 16);
                Color[] pixels = new Color[256];
                for (int y = 0; y < 16; y++)
                {
                    for (int x = 0; x < 16; x++)
                    {
                        float dist = Vector2.Distance(new Vector2(x, y), new Vector2(8, 8)) / 8f;
                        pixels[y * 16 + x] = dist < 1f ? Color.red : Color.clear;
                    }
                }
                _markerTexture.SetPixels(pixels);
                _markerTexture.Apply();
            }
        }

        void OnGUI()
        {
            var cam = Camera.main;
            if (cam == null) return;

            Vector3 screenPos = cam.WorldToScreenPoint(transform.position);
            
            // Check if in front of camera
            if (screenPos.z > 0)
            {
                // Convert to GUI coordinates (Y is flipped)
                float guiY = Screen.height - screenPos.y;
                
                // Draw marker
                float size = Mathf.Clamp(1000f / screenPos.z, 10f, 50f);  // Size based on distance
                Rect rect = new Rect(screenPos.x - size/2, guiY - size/2, size, size);
                
                GUI.color = Color.red;
                if (_markerTexture != null)
                {
                    GUI.DrawTexture(rect, _markerTexture);
                }
                else
                {
                    GUI.Box(rect, "");
                }
                
                // Draw distance label
                float distance = Vector3.Distance(cam.transform.position, transform.position);
                GUI.color = Color.white;
                GUI.Label(new Rect(screenPos.x - 50, guiY + size/2 + 5, 100, 20), 
                    $"ENEMY {distance:F0}m");
            }
        }
    }
}
