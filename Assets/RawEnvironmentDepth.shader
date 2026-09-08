Shader "Custom/RawEnvironmentDepth"
{
    // Displays the Depth API output as raw as possible: the sampled value is
    // written straight to RGB with no remapping, no linearization, no
    // normalization. Because depth is stored non-linearly (closer to the
    // camera uses more of the 0-1 range), most of the room will look very
    // bright/white and only nearby objects will show visible shading.
    //
    // Put this shader on a material, put that material on a quad placed in
    // front of the camera, and assign that quad's GameObject to
    // DepthApiRawViewer.depthDisplayObject.

    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Overlay" }

        Pass
        {
            ZTest Always
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing

            #include "UnityCG.cginc"

            // Raw depth texture provided globally by EnvironmentDepthManager
            // while it is enabled. One slice per eye (Texture2DArray).
            Texture2DArray _EnvironmentDepthTexture;
            SamplerState sampler_EnvironmentDepthTexture;

            struct AppData
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct VertexToFragment
            {
                float4 clipPosition : SV_POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            VertexToFragment Vert(AppData input)
            {
                VertexToFragment output;

                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                output.clipPosition = UnityObjectToClipPos(input.vertex);
                output.uv = input.uv;

                return output;
            }

            fixed4 Frag(VertexToFragment input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                // unity_StereoEyeIndex picks the correct array slice per eye.
                float arraySlice = unity_StereoEyeIndex;
                float3 sampleCoordinate = float3(input.uv, arraySlice);

                float rawDepthValue = _EnvironmentDepthTexture.Sample(
                    sampler_EnvironmentDepthTexture, sampleCoordinate).r;

                // No processing: raw value duplicated into R, G, B.
                return fixed4(rawDepthValue, rawDepthValue, rawDepthValue, 1.0);
            }
            ENDHLSL
        }
    }
}
