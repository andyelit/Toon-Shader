﻿using System.Collections;
using System.Collections.Generic;
using UnityEngine;

struct LightSet {
    public int id;
    public Light light;
    public Vector3 dir;
    public Color color;
    public float atten;
    public float inView;

    public LightSet(Light newLight) {
        light = newLight;
        id = newLight.GetInstanceID();
        dir = Vector3.zero;
        color = Color.black;
        color.a = 0.01f;
        atten = 0f;
        inView = 1.1f; // Range -0.1 to 1.1 which is clamped 0-1 to fade faster
    }
}

[ExecuteInEditMode]
public class ToonHelper : MonoBehaviour {

    // Params
    [SerializeField] bool raycast = true;
    [SerializeField] LayerMask raycastMask = new LayerMask();
    [SerializeField] float raycastFadeSpeed = 10f;
    [SerializeField] Vector3 meshCenter = Vector3.zero;
    [SerializeField] bool pause = false;
    [SerializeField] bool showGizmos = true;
    [SerializeField] int maxLights = 6;

    // State
    Vector3 posAbs;
    Dictionary<int, LightSet> lightSets;

    // Refs
    SkinnedMeshRenderer skinRenderer;
    MeshRenderer meshRenderer;
    Material material;
    Material tempMaterial;

    void Start() {
        skinRenderer = GetComponent<SkinnedMeshRenderer>();
        meshRenderer = GetComponent<MeshRenderer>();
        if (skinRenderer) {
            material = skinRenderer.material;
        } else if (meshRenderer) {
            material = meshRenderer.material;
        }
        GetLights();
    }

    // NOTE: If your game loads lights dynamically, this should be called to init new lights
    public void GetLights() {
        if (lightSets == null) {
            lightSets = new Dictionary<int, LightSet>();
        }

        Light[] lights = GameObject.FindObjectsOfType<Light>();
        List<int> newIds = new List<int>();

        // Initialise new lights
        foreach (Light light in lights) {
            int id = light.GetInstanceID();
            newIds.Add(id);
            if (!lightSets.ContainsKey(id)) {
                lightSets.Add(id, new LightSet(light));
            }
        }

        // Remove old lights
        List<int> oldIds = new List<int>(lightSets.Keys);
        foreach (int id in oldIds) {
            if (!newIds.Contains(id)) {
                lightSets.Remove(id);
            }
        }
    }

    void Update() {
        posAbs = transform.position + meshCenter;

        if (Application.isEditor && !Application.isPlaying) {
            EditorUpdate();
        } else {
            PlayUpdate();
        }
    }

    void PlayUpdate() {
        if (pause) return;
        UpdateLighting(material);
    }

    // While in editor, don't modify source material directly, make a copy
    void EditorUpdate() {
        // Revert to original material when paused
        if (pause) {
            if (tempMaterial) {
                skinRenderer.sharedMaterial = material;
                tempMaterial = null;
            }
            return;
        }

        // Create temporary material
        if (!tempMaterial) {
            if (!skinRenderer) skinRenderer = GetComponent<SkinnedMeshRenderer>();
            if (!meshRenderer) meshRenderer = GetComponent<MeshRenderer>();
            if (skinRenderer) tempMaterial = new Material(skinRenderer.sharedMaterial);
            if (meshRenderer) tempMaterial = new Material(meshRenderer.sharedMaterial);
            tempMaterial.name = "Generated Toon Shader";
            if (skinRenderer) skinRenderer.sharedMaterial = tempMaterial;
            if (meshRenderer) meshRenderer.sharedMaterial = tempMaterial;
        }

        GetLights();
        UpdateLighting(tempMaterial);
    }

    void UpdateLighting(Material mat) {

        // Refresh light data
        List<LightSet> sortedLights = new List<LightSet>();
        foreach (LightSet lightSet in lightSets.Values) {
            sortedLights.Add(CalcLight(lightSet));
        }

        // Sort lights by attenuation
        sortedLights.Sort((x, y) => y.atten.CompareTo(x.atten));

        // Apply lighting
        int i = 1;
        foreach (LightSet lightSet in sortedLights) {
            if (i > maxLights) break;
            if (lightSet.atten <= Mathf.Epsilon) break;

            // Use color Alpha to pass attenuation data
            Color color = lightSet.color;
            color.a = Mathf.Clamp(lightSet.atten, 0.01f, 0.99f); // UV might wrap around if attenuation is >1 or 0<

            mat.SetVector($"_L{i}_dir", lightSet.dir.normalized);
            mat.SetColor($"_L{i}_color", color);
            i++;
        }

        // Turn off the remaining light slots
        while (i <= maxLights) {
            mat.SetVector($"_L{i}_dir", Vector3.up);
            mat.SetColor($"_L{i}_color", Color.black);
            i++;
        }

        // Store updated light data
        foreach (LightSet lightSet in sortedLights) {
            lightSets[lightSet.id] = lightSet;
        }
    }

    LightSet CalcLight(LightSet lightSet) {
        Light light = lightSet.light;
        float inView = 1.1f;
        float dist;

        if (!light.isActiveAndEnabled) {
            lightSet.atten = 0f;
            return lightSet;
        }

        switch (light.type) {
            case LightType.Directional:
                lightSet.dir = light.transform.forward * -1f;
                inView = TestInView(lightSet.dir, 100f);
                lightSet.color = light.color * light.intensity;
                lightSet.atten = 1f;
                break;

            case LightType.Point:
                lightSet.dir = light.transform.position - posAbs;
                dist = Mathf.Clamp01(lightSet.dir.magnitude / light.range);
                inView = TestInView(lightSet.dir, lightSet.dir.magnitude);
                lightSet.atten = CalcAttenuation(dist);
                lightSet.color = light.color * lightSet.atten * light.intensity * 0.1f;
                break;

            case LightType.Spot:
                lightSet.dir = light.transform.position - posAbs;
                dist = Mathf.Clamp01(lightSet.dir.magnitude / light.range);
                float angle = Vector3.Angle(light.transform.forward * -1f, lightSet.dir.normalized);
                float inFront = Mathf.Lerp(0f, 1f, (light.spotAngle - angle * 2f) / lightSet.dir.magnitude); // More edge fade when far away from light source
                inView = inFront * TestInView(lightSet.dir, lightSet.dir.magnitude);
                lightSet.atten = CalcAttenuation(dist);
                lightSet.color = light.color * lightSet.atten * light.intensity * 0.05f;
                break;

            default:
                Debug.Log("Lighting type '" + light.type + "' not supported by Toon Helper");
                lightSet.atten = 0f;
                break;
        }

        // Slowly fade lights on and off
        float fadeSpeed = (Application.isEditor && !Application.isPlaying)
            ? raycastFadeSpeed / 60f
            : raycastFadeSpeed * Time.deltaTime;

        lightSet.inView = Mathf.Lerp(lightSet.inView, inView, fadeSpeed);
        lightSet.color *= Mathf.Clamp01(lightSet.inView);



        return lightSet;
    }

    float TestInView(Vector3 dir, float dist) {
        if (!raycast) return 1.1f;
        if (Physics.Raycast(posAbs, dir, dist, raycastMask)) {
            return -0.1f;
        } else {
            return 1.1f;
        }
    }

    // Ref - Light Attenuation calc: https://forum.unity.com/threads/light-attentuation-equation.16006/#post-3354254
    float CalcAttenuation(float dist) {
        return Mathf.Clamp01(1.0f / (1.0f + 25f * dist * dist) * Mathf.Clamp01((1f - dist) * 5f));
    }

    void OnDrawGizmos() {
        if (!showGizmos) return;

        // Visualise mesh center
        Gizmos.color = Color.white;
        Gizmos.DrawWireSphere(posAbs, 0.1f);

        // Visualise lighting
        if (lightSets != null) {
            List<LightSet> tmp = new List<LightSet>(lightSets.Values);
            foreach (LightSet lightSet in tmp) {
                Gizmos.color = lightSet.color;
                Gizmos.DrawRay(posAbs, lightSet.dir.normalized * lightSet.atten * 2f);
            }
        }
    }
}
