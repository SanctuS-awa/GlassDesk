// App-owned wallpaper material. The input is a bounded local image texture;
// only this shader samples it. No system backdrop or screen-capture source.
cbuffer Frame : register(b0)
{
    float4 dimensions;      // output width/height in px, dpi scale x/y
    float4 body;            // body x/y/width/height in output px
    float4 shape;           // radius DIP, surface tension, transition, base zoom
    float4 material;        // bend, softness DIP, tint, light intensity
    float4 wallpaper;       // displayed image left/top/width/height in desktop px
    float4 window;           // window desktop origin x/y, source alpha mode, reserved
    float4 flags;            // peek, music controls, lens count, reserved
    float4 backgroundColor;  // letterbox RGB, reserved
    float4 lensRect[3];      // local pixel x/y/width/height
    float4 lensStyle[3];     // radius DIP, opacity, light tone, emphasis
    float4 blurParams;       // blur step x/y, source-pixel sigma, source mip
};
Texture2D<float4> backdrop : register(t0);
Texture2D<float4> softenedBackdrop : register(t1);
SamplerState imageSampler : register(s0);

struct Varying
{
    float4 position : SV_POSITION;
    float2 uv : TEXCOORD0;
};

Varying VS(uint id : SV_VertexID)
{
    Varying output;
    float2 uv = float2((id << 1) & 2, id & 2);
    output.position = float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
    output.uv = uv;
    return output;
}

float4 PSBlur(Varying input) : SV_TARGET
{
    float4 colour = 0;
    float total = 0;
    float sigma = max(blurParams.z, 0.001);
    [unroll]
    for (int i = -10; i <= 10; ++i)
    {
        float weight = exp(-0.5 * i * i / (sigma * sigma));
        float4 texel = backdrop.SampleLevel(
            imageSampler,
            input.uv + blurParams.xy * i,
            blurParams.w);
        if (window.z < 0.5)
            texel.a = 1;
        colour += texel * weight;
        total += weight;
    }
    return colour / total;
}

// The optical equations below mirror the accepted PreviewGlass.hlsl profile;
// keep its 0.08 zoom, 18-30 DIP band, 0.62 dome, 1.45 IOR, 0.35 DIP
// softness, 1.0 bend, and zero tint in sync. Product shape uses the host SDF.
float Phase(float p, float a, float b)
{
    float t = saturate((p - a) / (b - a));
    return t * t * (3 - 2 * t);
}

float Round(float2 p, float2 c, float2 size, float radius)
{
    radius = min(radius, min(size.x, size.y) * 0.5);
    float2 q = abs(p - c) - size * 0.5 + radius;
    return length(max(q, 0)) + min(max(q.x, q.y), 0) - radius;
}

float Mask(float distance, float aa)
{
    return 1 - smoothstep(-aa, aa, distance);
}

// Port of SurfaceTension.h. The caller provides the current surface dimensions,
// radius and tension so the material edge matches the host's geometry.
float TensionFactor(float y, float width, float height, float dpiScale, float strain)
{
    if (width <= 0 || height <= 0)
        return 1;
    float v = saturate(y / height);
    float belly = 4 * v * (1 - v);
    float direction = strain >= 0 ? 1 : -1;
    float amount = abs(strain) * belly * belly * (0.78 + 0.22 * direction * (2 * v - 1));
    float limit = min(7 * dpiScale, min(height * 0.035, width * 0.018));
    return 1 - min(amount, 1.15) * limit / (width * 0.5);
}

float SurfaceRadius(float width, float height, float radius, float strain)
{
    float halfSize = min(width, height) * 0.5;
    radius = clamp(radius, 0, halfSize);
    return radius + (halfSize - radius) * 0.20 * min(abs(strain), 1);
}

float SurfaceDistance(float2 p, float2 size, float radiusDip, float dpiScale, float strain)
{
    float radius = SurfaceRadius(size.x, size.y, radiusDip * dpiScale, strain);
    float factor = TensionFactor(p.y, size.x, size.y, dpiScale, strain);
    p.x = size.x * 0.5 + (p.x - size.x * 0.5) / factor;
    float2 q = abs(p - size * 0.5) - size * 0.5 + radius;
    return (length(max(q, 0)) + min(max(q.x, q.y), 0) - radius) * factor;
}

float MainDistance(float2 pPx)
{
    float dpiScale = max(dimensions.z, dimensions.w);
    return SurfaceDistance(
        pPx - body.xy,
        body.zw,
        shape.x,
        dpiScale,
        shape.y);
}

float2 MainNormal(float2 pPx)
{
    float dpiScale = max(dimensions.z, dimensions.w);
    float stepPx = 0.4 * dpiScale;
    float2 normal = float2(
        MainDistance(pPx + float2(stepPx, 0)) - MainDistance(pPx - float2(stepPx, 0)),
        MainDistance(pPx + float2(0, stepPx)) - MainDistance(pPx - float2(0, stepPx)));
    return normal / max(length(normal), 0.0001);
}

// One shallow convex interface drives both refraction and illumination.
float3 SurfaceNormal(float depth, float2 outward, float band, float height)
{
    float u = 1 - saturate(depth / band), slope = height / band * u / sqrt(max(1 - u * u, .003));
    return normalize(float3(outward * slope, 1));
}

float2 LensSample(
    float2 p,
    float2 centre,
    float distance,
    float2 outward,
    float zoom,
    float bend,
    float band,
    bool primary)
{
    float dpiScale = max(dimensions.z, dimensions.w);
    float depth = max(0, -distance) / dpiScale;
    float bandDip = band / dpiScale;
    float height = bandDip * (primary ? 0.62 : 0.46);
    float3 normal = SurfaceNormal(depth, outward, bandDip, height);
    float3 ray = refract(float3(0, 0, -1), normal, 1 / 1.45);
    float cap = sqrt(saturate(1 - pow(1 - saturate(depth / bandDip), 2)));
    float2 edge = ray.xy / max(-ray.z, 0.1)
        * (bandDip * (primary ? 0.62 : 0.48) + height * cap)
        * bend
        * Phase(depth, 0, primary ? 1.5 : 3);
    float weight = primary
        ? Phase(depth, bandDip * 0.6, bandDip + 6)
        : Phase(depth, 0, bandDip);
    return p - (p - centre) * (1 - 1 / max(zoom, 1)) * weight + edge * dpiScale;
}

float2 MainSample(float2 pPx, float distance, float2 normal)
{
    float dpiScale = max(dimensions.z, dimensions.w);
    float transition = saturate(shape.z);
    float energy = saturate(abs(shape.y) / 8);
    float zoom = 1 + shape.w * (1 + 0.12 * transition + 0.12 * energy);
    float band = lerp(18, 30, Phase(transition, 0.08, 1)) * dpiScale;
    float2 centre = body.xy + body.zw * 0.5;
    return LensSample(
        pPx,
        centre,
        distance,
        normal,
        zoom,
        material.x * (1 + 0.12 * energy),
        band,
        true);
}

float3 Background()
{
    return saturate(backgroundColor.rgb);
}

float3 SoftWallpaper(float2 localPx)
{
    float2 screenPx = window.xy + localPx;
    float2 uv = (screenPx - wallpaper.xy) / wallpaper.zw;
    if (any(uv < 0) || any(uv > 1))
        return Background();

    float4 sample = softenedBackdrop.SampleLevel(imageSampler, uv, 0);
    if (window.z < 0.5)
        return saturate(sample.rgb);
    return saturate(sample.rgb + Background() * (1 - saturate(sample.a)));
}
float3 LowerGlass(float2 pPx)
{
    float distance = MainDistance(pPx);
    float2 sample = MainSample(pPx, distance, MainNormal(pPx));
    float3 colour = SoftWallpaper(sample);
    return lerp(colour, float3(0.985, 0.99, 1), saturate(material.z));
}

float3 Light(
    float3 colour,
    float distance,
    float2 outward,
    float band,
    float emphasis,
    bool primary)
{
    float dpiScale = max(dimensions.z, dimensions.w);
    float depth = max(0, -distance) / dpiScale;
    float bandDip = band / dpiScale;
    float3 normal = SurfaceNormal(depth, outward, bandDip, bandDip * (primary ? 0.62 : 0.46));
    float highlight = pow(saturate(dot(normal, normalize(float3(-0.48, -0.64, 1)))), primary ? 10 : 20);
    float fresnel = pow(1 - normal.z, 3);
    float rim = exp(-pow((depth - (primary ? 1.1 : 0.65))
        / (primary ? 1.3 : 0.65), 2));
    float key = pow(saturate(dot(outward, normalize(float2(-0.55, -0.83)))), 3);
    float counter = pow(saturate(dot(outward, normalize(float2(0.65, 0.76)))), 5);
    colour = lerp(
        colour,
        1,
        saturate((highlight * (primary ? 0.09 : 0.13)
            + rim * (key * (primary ? 0.13 : 0.53) + counter * (primary ? 0.015 : 0.18))
            + fresnel * (primary ? 0.035 : 0.075)) * emphasis));
    return primary
        ? colour
        : colour * (1 - exp(-pow((depth - 2.4) / 1.6, 2)) * (1 - key) * 0.028 * emphasis);
}

float4 PS(Varying input) : SV_TARGET
{
    float2 pPx = input.position.xy;
    float dpiScale = max(dimensions.z, dimensions.w);
    float distance = MainDistance(pPx);
    float aa = max(0.5, 0.25 * dpiScale);
    float coverage = Mask(distance, aa);
    float3 result = Background();

    if (coverage > 0)
    {
        float2 normal = MainNormal(pPx);
        float2 sample = MainSample(pPx, distance, normal);
        result = SoftWallpaper(sample);
        result = lerp(result, float3(0.985, 0.99, 1), saturate(material.z));

        float transition = saturate(shape.z);
        float band = lerp(18, 30, Phase(transition, 0.08, 1)) * dpiScale;
        if (material.w > 0.001)
            result = Light(result, distance, normal, band, material.w, true);

        // The three local controls are optional and read the lower material
        // after the main lens mapping. Foreground symbols remain host content.
        if (flags.x > 0.5 && flags.y > 0.5 && MainDistance(pPx) <= 0)
        {
            [unroll]
            for (int j = 0; j < 3; ++j)
            {
                if (j >= (int)flags.z)
                    continue;

                float4 lens = lensRect[j];
                float4 style = lensStyle[j];
                if (lens.z <= 0 || lens.w <= 0 || style.y <= 0)
                    continue;

                float2 centre = lens.xy + lens.zw * 0.5;
                float radius = style.x * dpiScale;
                float lensDistance = Round(pPx, centre, lens.zw, radius);
                if (lensDistance > 8 * dpiScale)
                    continue;

                float2 q = abs(pPx - centre) - lens.zw * 0.5 + radius;
                float2 outward;
                if (length(max(q, 0)) > 0.001)
                    outward = normalize(max(q, 0)) * sign(pPx - centre);
                else
                    outward = q.x > q.y
                        ? float2(sign(pPx.x - centre.x), 0)
                        : float2(0, sign(pPx.y - centre.y));

                float emphasis = max(style.w, 0);
                float zoom = 1.14 + 0.055 * emphasis;
                float bend = 0.85 + 0.25 * emphasis;
                float band = max(3 * dpiScale, min(lens.z, lens.w) * 0.42);
                float2 localSample = LensSample(
                    pPx,
                    centre,
                    lensDistance,
                    outward,
                    zoom,
                    bend,
                    band,
                    false);

                float3 glass = LowerGlass(localSample);
                glass = lerp(
                    glass,
                    style.z > 0.5 ? float3(1, 1, 1) : float3(0.03, 0.04, 0.06),
                    style.z > 0.5 ? 0.13 : 0.07);
                float crown = saturate(0.5 - (pPx.y - centre.y) / max(lens.w, 1));
                glass = lerp(glass, 1, crown * crown * (0.055 + 0.025 * emphasis));
                if (material.w > 0.001)
                    glass = Light(glass, lensDistance, outward, band, 1.05 + 0.3 * emphasis, false);

                result = lerp(result, glass, Mask(lensDistance, aa) * saturate(style.y));
            }
        }
    }

    // One coverage mask owns the antialiased silhouette. Premultiplied output
    // makes the outside fully transparent without a wallpaper rectangle.
    return float4(saturate(result) * coverage, coverage);
}









