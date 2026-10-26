namespace SkinTextureTool.Rendering;

/// <summary>
/// GLSL for the generator. Everything renders in UV space: each triangle is drawn at its UV position so every
/// texel knows its point on the body, and the layers run per texel. Textures are stored with v = 0 in row 0,
/// matching the DDS files, so all of them are sampled with the mesh's (u, v) directly.
/// </summary>
internal static class Shaders
{
    /// <summary>Draws the mesh flattened into its UV layout, carrying surface data to the fragment stage.</summary>
    public const string UvVertex = """
        #version 330 core
        layout(location=0) in vec3 aPos;
        layout(location=1) in vec2 aUv;
        layout(location=2) in vec3 aNrm;
        layout(location=3) in vec3 aTan;
        layout(location=4) in vec3 aBit;
        layout(location=5) in vec2 aUvScale;
        out vec3 vPos; out vec2 vUv; out vec3 vNrm; out vec3 vTan; out vec3 vBit; out vec2 vUvScale;
        void main() {
            vPos = aPos; vUv = aUv; vNrm = aNrm; vTan = aTan; vBit = aBit; vUvScale = aUvScale;
            gl_Position = vec4(aUv.x * 2.0 - 1.0, aUv.y * 2.0 - 1.0, 0.0, 1.0);
        }
        """;

    /// <summary>Hash-based gradient noise, fBm and Voronoi, written for this tool.</summary>
    private const string Noise = """
        uniform uint uSeed;
        uint hash1(uint x) { x ^= x >> 16; x *= 0x7feb352du; x ^= x >> 15; x *= 0x846ca68bu; x ^= x >> 16; return x; }
        uint hash3(ivec3 c) { return hash1(uint(c.x) ^ hash1(uint(c.y) ^ hash1(uint(c.z) ^ uSeed))); }
        float rand01(uint h) { return float(h & 0xffffffu) / 16777215.0; }
        vec3 randVec(ivec3 c) {
            uint h = hash3(c);
            return vec3(rand01(h), rand01(hash1(h + 1u)), rand01(hash1(h + 2u)));
        }
        vec3 gradDir(ivec3 c) { return normalize(randVec(c) * 2.0 - 1.0 + 1e-5); }
        // gradient noise in about [-1, 1]
        float gnoise(vec3 p) {
            ivec3 i = ivec3(floor(p)); vec3 f = fract(p);
            vec3 u = f * f * f * (f * (f * 6.0 - 15.0) + 10.0);
            float n000 = dot(gradDir(i), f);
            float n100 = dot(gradDir(i + ivec3(1,0,0)), f - vec3(1,0,0));
            float n010 = dot(gradDir(i + ivec3(0,1,0)), f - vec3(0,1,0));
            float n110 = dot(gradDir(i + ivec3(1,1,0)), f - vec3(1,1,0));
            float n001 = dot(gradDir(i + ivec3(0,0,1)), f - vec3(0,0,1));
            float n101 = dot(gradDir(i + ivec3(1,0,1)), f - vec3(1,0,1));
            float n011 = dot(gradDir(i + ivec3(0,1,1)), f - vec3(0,1,1));
            float n111 = dot(gradDir(i + ivec3(1,1,1)), f - vec3(1,1,1));
            return 1.6 * mix(mix(mix(n000, n100, u.x), mix(n010, n110, u.x), u.y),
                             mix(mix(n001, n101, u.x), mix(n011, n111, u.x), u.y), u.z);
        }
        float fbm(vec3 p, int octaves) {
            float sum = 0.0, amp = 0.5, norm = 0.0;
            for (int o = 0; o < octaves; o++) { sum += amp * gnoise(p); norm += amp; p = p * 2.03 + 17.1; amp *= 0.5; }
            return sum / norm;
        }
        // x = distance to nearest feature point, y = to second nearest, z = random value of the nearest cell
        vec3 voronoi(vec3 p) {
            ivec3 i = ivec3(floor(p)); vec3 f = fract(p);
            float d1 = 8.0, d2 = 8.0; float id = 0.0;
            for (int z = -1; z <= 1; z++) for (int y = -1; y <= 1; y++) for (int x = -1; x <= 1; x++) {
                ivec3 c = i + ivec3(x, y, z);
                vec3 r = vec3(x, y, z) + randVec(c) - f;
                float d = dot(r, r);
                if (d < d1) { d2 = d1; d1 = d; id = rand01(hash1(hash3(c) + 7u)); }
                else if (d < d2) d2 = d;
            }
            return vec3(sqrt(d1), sqrt(d2), id);
        }
        """;

    /// <summary>
    /// The skin layers. Outputs: 0 albedo (a = coverage), 1 height in world units (a = coverage),
    /// 2 specular (r) and 3 subsurface tint.
    /// </summary>
    public const string GenerateShell = "#version 330 core\n#define SHELL\n" + Noise + GenerateBody;
    public const string Generate = "#version 330 core\n" + Noise + GenerateBody;
    private const string GenerateBody = """
        in vec3 vPos; in vec2 vUv; in vec3 vNrm; in vec3 vTan; in vec3 vBit; in vec2 vUvScale;
        layout(location=0) out vec4 oAlbedo;
        layout(location=1) out vec4 oHeight;
        layout(location=2) out vec4 oSpec;
        layout(location=3) out vec4 oSss;
        layout(location=4) out vec4 oRfaos;

        uniform float uScale; uniform int uUseUv;
        // World units per texel of the coarsest texture group. Fine detail is band-limited to it, so meshes
        // with very different texel densities (a high-poly head next to the body) draw identical detail.
        uniform float uFootprint;
        uniform vec3 uBaseColor; uniform float uMottling, uMottlingScale;
        // zones: x strength, y scale, z enabled
        uniform vec3 uRedColor, uYellowColor, uBlueColor; uniform vec3 uRed, uYellow, uBlue;
        uniform vec3 uLipsColor; uniform vec2 uLips;
        uniform vec4 uPores;   // strength, size, density, dirt
        uniform vec3 uCells;   // strength, scale, enabled
        uniform vec3 uGoose;   // strength, scale, enabled
        // spots: amount, scale, strength, bump
        uniform vec3 uFreckColor, uMoleColor, uPimpleColor, uBlemColor, uRedSpotColor;
        uniform vec4 uFreck, uMole, uPimple, uBlem, uRedSpot;
        uniform vec3 uVeinColor; uniform vec3 uVeins; uniform float uVeinBump; // strength, scale, enabled; bump
        // fur: colour, pattern colour, belly colour; (enabled, colour strength, colour variation, density),
        // (strand length, spread, normal strength, light belly), (stripes, stripe scale, spots, spot scale)
        uniform vec3 uFurColor, uFurPatternColor, uFurBellyColor; uniform vec4 uFur, uFur2, uFur3;
        uniform sampler2D mFur; uniform int hFur;
        uniform vec4 uMaterial; uniform vec3 uSssColor; // spec, specVariation, subsurface, -
        uniform vec4 uPbr; // roughness, roughness variation, fuzz, cavity occlusion
        // the meshes' current textures to start from, with a strength each (diffuse, specular, subsurface)
        uniform sampler2D uExAlbedo, uExSpec, uExSss; uniform int hExAlbedo, hExSpec, hExSss; uniform vec3 uExMix;

        // masks: sampler + "has mask" flag; without a mask a layer applies everywhere it otherwise would
        uniform sampler2D mRed, mYellow, mBlue, mLips, mPores, mFreck, mMole, mPimple, mBlem, mRedSpot, mVeins;
        uniform int hRed, hYellow, hBlue, hLips, hPores, hFreck, hMole, hPimple, hBlem, hRedSpot, hVeins;
        float mask(sampler2D s, int has) { return has == 1 ? texture(s, vUv).r : 1.0; }

        // How many texels (at the shared footprint) one noise cell of this frequency spans.
        float cellTexels(float freq) { return 1.0 / max(freq * uScale * uFootprint, 1e-6); }
        // Fades detail whose cells are too small to draw cleanly at the shared footprint.
        float detailFade(float freq) { return smoothstep(2.0, 6.0, cellTexels(freq)); }

        // A sparse spot field: cells at this frequency hold a spot with probability 'amount', of radius 'radius'
        // (in cell units). Edges are softened to at least a texel of the shared footprint.
        float spots(vec3 p, float freq, float amount, float radius, float seedShift) {
            vec3 v = voronoi(p * freq + seedShift);
            if (v.z > amount) return 0.0;
            float w = 1.2 / cellTexels(freq);
            return smoothstep(radius + w * 0.5, max(radius * 0.35 - w * 0.5, 0.0), v.x);
        }

        void main() {
            // coordinates in body units (about 1.4 cm each); UV mode maps the 0..1 layout onto a 100-unit square
            vec3 p = (uUseUv == 1 ? vec3(vUv * 100.0, 0.0) : vPos) * uScale;
            vec3 col = uBaseColor;
            if (hExAlbedo == 1) col = mix(col, texture(uExAlbedo, vUv).rgb, uExMix.x);
            float height = 0.0;
            float poreAmount = 0.0;
            float cavity = 0.0; // how recessed this texel is, for ambient occlusion

            // broad mottling: slow light/dark and warm/cool drift
            float m = fbm(p * 0.04 * uMottlingScale, 4);
            col *= 1.0 + uMottling * 0.18 * m;
            col = mix(col, col * vec3(1.04, 0.96, 0.94), uMottling * smoothstep(-0.3, 0.6, fbm(p * 0.09 * uMottlingScale + 31.0, 3)));

            // colour zones: soft noise-shaped regions, optionally limited by masks
            if (uRed.z > 0.5) col = mix(col, uRedColor, uRed.x * smoothstep(-0.1, 0.7, fbm(p * 0.03 * uRed.y + 3.0, 4)) * mask(mRed, hRed));
            if (uYellow.z > 0.5) col = mix(col, uYellowColor, uYellow.x * smoothstep(-0.1, 0.7, fbm(p * 0.03 * uYellow.y + 11.0, 4)) * mask(mYellow, hYellow));
            if (uBlue.z > 0.5) col = mix(col, uBlueColor, uBlue.x * smoothstep(0.0, 0.8, fbm(p * 0.05 * uBlue.y + 23.0, 4)) * mask(mBlue, hBlue));
            if (uLips.y > 0.5 && hLips == 1) col = mix(col, uLipsColor, uLips.x * texture(mLips, vUv).r);

            // blemishes: broad uneven patches
            if (uBlem.x > 0.0) {
                float b = smoothstep(1.0 - uBlem.x, 1.0, fbm(p * 0.08 * uBlem.y + 41.0, 4) * 0.5 + 0.5);
                col = mix(col, uBlemColor, uBlem.z * b * mask(mBlem, hBlem));
            }
            // red spots: small irritated patches
            if (uRedSpot.x > 0.0) {
                float r = spots(p, 0.35 * uRedSpot.y, uRedSpot.x, 0.45, 53.0);
                col = mix(col, uRedSpotColor, uRedSpot.z * r * mask(mRedSpot, hRedSpot));
            }
            // freckles: clustered where a slow noise allows them
            if (uFreck.x > 0.0) {
                float cluster = smoothstep(-0.2, 0.5, fbm(p * 0.05 * uFreck.y + 61.0, 3));
                float f = spots(p, 1.1 * uFreck.y, uFreck.x * cluster, 0.42, 67.0);
                col = mix(col, uFreckColor, uFreck.z * f * mask(mFreck, hFreck));
            }
            // veins: thin ridges of a low-frequency noise
            if (uVeins.z > 0.5 && uVeins.x > 0.0) {
                float r = 1.0 - abs(fbm(p * 0.06 * uVeins.y + 71.0, 3));
                float v = pow(clamp(r, 0.0, 1.0), 40.0);
                col = mix(col, uVeinColor, uVeins.x * v * mask(mVeins, hVeins));
                height += v * uVeinBump * 0.03 * mask(mVeins, hVeins);
            }
            // moles: rare dark spots, slightly raised
            if (uMole.x > 0.0) {
                float mo = spots(p, 0.12 * uMole.y, uMole.x * 0.25, 0.1, 83.0) * mask(mMole, hMole);
                col = mix(col, uMoleColor, uMole.z * mo);
                height += mo * uMole.w * 0.06;
            }
            // pimples: small reddish bumps
            if (uPimple.x > 0.0) {
                float pi = spots(p, 0.3 * uPimple.y, uPimple.x * 0.4, 0.3, 97.0) * mask(mPimple, hPimple);
                col = mix(col, uPimpleColor, uPimple.z * pi);
                height += pi * uPimple.w * 0.05;
            }
            // pores: tiny pits, denser in some areas, holding a little dirt
            if (uPores.x > 0.0) {
                float density = smoothstep(-0.5, 0.6, fbm(p * 0.07 + 101.0, 3));
                vec3 v = voronoi(p * 4.5 * uPores.z + 107.0);
                float pw = 1.2 / cellTexels(4.5 * uPores.z);
                float pore = (v.z < 0.35 + 0.6 * density ? 1.0 : 0.0) * smoothstep(0.22 * uPores.y + pw * 0.5, max(0.04 - pw * 0.5, 0.0), v.x);
                pore *= mask(mPores, hPores) * detailFade(4.5 * uPores.z);
                poreAmount = pore;
                cavity = max(cavity, pore * uPores.x);
                height -= pore * uPores.x * 0.012;
                col *= 1.0 - pore * uPores.w * 0.35;
            }
            // skin cells: a fine network of shallow creases
            if (uCells.z > 0.5 && uCells.x > 0.0) {
                vec3 v = voronoi(p * 1.6 * uCells.y + 113.0);
                float cw = 1.2 / cellTexels(1.6 * uCells.y);
                float crease = smoothstep(0.12 + cw, 0.0, v.y - v.x) * detailFade(1.6 * uCells.y);
                height -= crease * uCells.x * 0.006;
                cavity = max(cavity, crease * uCells.x * 0.5);
            }
            // goosebumps: small raised dots
            if (uGoose.z > 0.5 && uGoose.x > 0.0) {
                vec3 v = voronoi(p * 2.5 * uGoose.y + 127.0);
                height += (v.z < 0.6 ? 1.0 : 0.0) * smoothstep(0.38, 0.0, v.x) * uGoose.x * 0.01 * detailFade(2.5 * uGoose.y);
            }

            // fur: strands follow a flow over the surface (down the body, sweeping away from the spine), drawn as
            // cells stretched along it, each with its own height (the shell texture's alpha)
            vec4 shell = vec4(0.0);
            if (uFur.x > 0.5) {
                float cover = mask(mFur, hFur);
                vec3 n = normalize(vNrm);
                vec3 flow = vec3(0.0, 0.0, -1.0) + vec3(sign(vPos.x + 1e-4), 0.0, 0.0) * uFur2.y * 1.5;
                flow = flow - n * dot(flow, n);
                flow = length(flow) > 1e-3 ? normalize(flow) : normalize(cross(n, vec3(1, 0, 0)));
                vec3 side = normalize(cross(n, flow));
                // strands are as fine as the density asks, but never under about 2.5 texels at this resolution:
                // finer than that can't be drawn, and the shells need real strands, not a blur
                float strandFreq = min(12.0 * uFur.w, 1.0 / max(2.5 * uScale * uFootprint, 1e-6));
                vec3 q = vec3(dot(vPos, flow) / uFur2.x, dot(vPos, side), dot(vPos, n)) * (strandFreq * uScale);
                vec3 cell = voronoi(q + 151.0);
                // a strand fills most of its cell, tapering at the edge, so the coat reads full rather than patchy
                float core = smoothstep(1.15, 0.0, cell.x);
                float strandHeight = pow(cell.z, 0.7) * core; // more strands reach the outer shells
                float fade = 1.0;
                // colour: fur over the skin, with stripes, spots, a lighter belly and per-strand variation
                vec3 fur = uFurColor * (1.0 + uFur.z * 0.35 * fbm(p * 0.06 + 157.0, 3));
                if (uFur3.x > 0.0) {
                    float band = sin(vPos.z * 0.45 * uFur3.y + fbm(p * 0.04 * uFur3.y + 163.0, 3) * 3.0 + abs(vPos.x) * 0.12 * uFur3.y);
                    fur = mix(fur, uFurPatternColor, uFur3.x * smoothstep(0.35, 0.8, band));
                }
                if (uFur3.z > 0.0)
                    fur = mix(fur, uFurPatternColor, uFur3.z * spots(p, 0.18 * uFur3.w, 0.55, 0.55, 167.0));
                if (uFur2.w > 0.0) {
                    float belly = smoothstep(0.25, 0.85, n.y) * smoothstep(40.0, 70.0, vPos.z) * (1.0 - smoothstep(108.0, 116.0, vPos.z));
                    float throat = smoothstep(0.2, 0.8, n.y) * smoothstep(104.0, 112.0, vPos.z);
                    fur = mix(fur, uFurBellyColor, uFur2.w * max(belly, throat));
                }
                fur *= mix(1.0, 0.82 + 0.25 * strandHeight, fade);
                col = mix(col, fur, uFur.y * cover);
                height -= (1.0 - core) * uFur2.z * 0.004 * cover * fade;
                shell = vec4(clamp(fur, 0.0, 1.0), clamp(mix(0.5, strandHeight, fade) * cover, 0.0, 1.0));
            }

            float spec = uMaterial.x * (1.0 - uMaterial.y * (0.5 + 0.5 * fbm(p * 0.2 + 131.0, 3)));
            if (hExSpec == 1) spec = mix(spec, texture(uExSpec, vUv).r, uExMix.y);
            spec *= 1.0 - poreAmount * 0.5;

            oAlbedo = vec4(clamp(col, 0.0, 1.0), 1.0);
            oHeight = vec4(height, 0.0, 0.0, 1.0);
            oSpec = vec4(vec3(clamp(spec, 0.0, 1.0)), 1.0);
            vec3 sss = uSssColor * uMaterial.z;
            if (hExSss == 1) sss = mix(sss, texture(uExSss, vUv).rgb, uExMix.z);
            oSss = vec4(sss, 1.0);
            float rough = uPbr.x + uPbr.y * 0.3 * fbm(p * 0.15 + 137.0, 3) + poreAmount * 0.15;
            float fuzz = uPbr.z * smoothstep(-0.4, 0.4, fbm(p * 0.05 + 139.0, 3));
            float ao = 1.0 - uPbr.w * cavity;
            oRfaos = vec4(clamp(rough, 0.04, 1.0), clamp(fuzz, 0.0, 1.0), clamp(ao, 0.0, 1.0), clamp(spec, 0.0, 1.0));
        #ifdef SHELL
            oAlbedo = shell; // the shell pass writes only the fur shell texture
        #endif
        }
        """;

    /// <summary>
    /// Model-space normal bake, after MuDynamicNormalMap's UpdateNormalMap pass: the smoothed mesh normal is bent
    /// by a tangent-space detail normal (here from the generated height, plus an optional detail map) and
    /// written in Skyrim's model-space layout, x/z/y in r/g/b.
    /// </summary>
    public const string BakeNormals = """
        #version 330 core
        in vec3 vPos; in vec2 vUv; in vec3 vNrm; in vec3 vTan; in vec3 vBit; in vec2 vUvScale;
        layout(location=0) out vec4 oNormal;
        uniform sampler2D uHeight; uniform vec2 uTexel; uniform float uDetailStrength;
        uniform sampler2D uDetailMap; uniform int uHasDetailMap; uniform float uDetailMapStrength; uniform int uZCorrection;
        // an existing model-space normal map to start from instead of the bare mesh normal
        uniform sampler2D uExNormal; uniform int hExNormal; uniform float uExNormalMix;

        float h(vec2 uv, float centre) { vec4 s = texture(uHeight, uv); return s.a > 0.5 ? s.r : centre; }

        void main() {
            vec3 n = normalize(vNrm);
            if (hExNormal == 1) {
                vec3 e = texture(uExNormal, vUv).rgb * 2.0 - 1.0; // stored x, z, y
                n = normalize(mix(n, normalize(vec3(e.r, e.b, e.g)), uExNormalMix));
            }
            vec3 t = vTan; vec3 b = vBit;
            vec3 ft = length(t) > 0.0 ? normalize(t - n * dot(n, t)) : vec3(1, 0, 0);
            float handed = dot(cross(n, ft), b) < 0.0 ? -1.0 : 1.0;
            vec3 fb = normalize(cross(n, ft)) * handed;
            mat3 tbn = mat3(ft, fb, n);

            // slope of the generated height along the surface, per world unit
            float hc = texture(uHeight, vUv).r;
            float dhdu = (h(vUv + vec2(uTexel.x, 0), hc) - h(vUv - vec2(uTexel.x, 0), hc)) / (2.0 * uTexel.x * max(vUvScale.x, 1e-4));
            float dhdv = (h(vUv + vec2(0, uTexel.y), hc) - h(vUv - vec2(0, uTexel.y), hc)) / (2.0 * uTexel.y * max(vUvScale.y, 1e-4));
            vec3 detail = normalize(vec3(-dhdu * uDetailStrength, -dhdv * uDetailStrength, 1.0));

            if (uHasDetailMap == 1) {
                vec4 d = texture(uDetailMap, vUv);
                vec3 m = d.rgb * 2.0 - 1.0;
                if (uZCorrection == 1) m.z = sqrt(max(0.0, 1.0 - m.x * m.x - m.y * m.y));
                m = normalize(mix(vec3(0, 0, 1), m, uDetailMapStrength));
                detail = normalize(vec3(detail.xy + m.xy, detail.z * m.z)); // whiteout blend
            }

            vec3 result = normalize(tbn * detail);
            oNormal = vec4((result * 0.5 + 0.5).xzy, 1.0);
        }
        """;

    /// <summary>Full-screen triangle for the image passes.</summary>
    public const string ScreenVertex = """
        #version 330 core
        out vec2 vUv;
        void main() {
            vec2 p = vec2((gl_VertexID << 1) & 2, gl_VertexID & 2);
            vUv = p; gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);
        }
        """;

    /// <summary>
    /// Bleeds colour past the UV island edges (MuDynamicNormalMap's BleedTexture): an empty texel takes the
    /// average of its filled neighbours, so mipmaps and filtering don't pull in black along seams.
    /// </summary>
    public const string Bleed = """
        #version 330 core
        in vec2 vUv;
        layout(location=0) out vec4 o0; layout(location=1) out vec4 o1; layout(location=2) out vec4 o2;
        layout(location=3) out vec4 o3; layout(location=4) out vec4 o4;
        uniform sampler2D t0, t1, t2, t3, t4;
        // coverage is the alpha of t0 (albedo); t4 (RFAOS) uses its alpha for specular, so it follows t0's coverage
        void main() {
            ivec2 c = ivec2(gl_FragCoord.xy);
            vec4 a = texelFetch(t0, c, 0);
            if (a.a >= 0.999) { o0 = a; o1 = texelFetch(t1, c, 0); o2 = texelFetch(t2, c, 0); o3 = texelFetch(t3, c, 0); o4 = texelFetch(t4, c, 0); return; }
            vec4 s0 = vec4(0), s1 = vec4(0), s2 = vec4(0), s3 = vec4(0), s4 = vec4(0); int n = 0;
            ivec2 size = textureSize(t0, 0);
            for (int y = -1; y <= 1; y++) for (int x = -1; x <= 1; x++) {
                if (x == 0 && y == 0) continue;
                ivec2 q = c + ivec2(x, y);
                if (any(lessThan(q, ivec2(0))) || any(greaterThanEqual(q, size))) continue;
                vec4 q0 = texelFetch(t0, q, 0);
                if (q0.a < 0.999) continue;
                s0 += q0; s1 += texelFetch(t1, q, 0); s2 += texelFetch(t2, q, 0); s3 += texelFetch(t3, q, 0); s4 += texelFetch(t4, q, 0); n++;
            }
            if (n == 0) { o0 = a; o1 = texelFetch(t1, c, 0); o2 = texelFetch(t2, c, 0); o3 = texelFetch(t3, c, 0); o4 = texelFetch(t4, c, 0); return; }
            float k = 1.0 / float(n);
            o0 = vec4((s0 * k).rgb, 1.0); o1 = vec4((s1 * k).rgb, 1.0); o2 = vec4((s2 * k).rgb, 1.0); o3 = vec4((s3 * k).rgb, 1.0); o4 = s4 * k;
        }
        """;

    /// <summary>
    /// Blends a region's layers over the base: each output mixes towards the region's value by
    /// mask × strength, keeping the base's coverage in alpha (RFAOS alpha is specular, so it blends too).
    /// </summary>
    public const string Composite = """
        #version 330 core
        in vec2 vUv;
        layout(location=0) out vec4 o0; layout(location=1) out vec4 o1; layout(location=2) out vec4 o2;
        layout(location=3) out vec4 o3; layout(location=4) out vec4 o4;
        uniform sampler2D b0, b1, b2, b3, b4, r0, r1, r2, r3, r4, uMask;
        uniform float uStrength;
        void main() {
            ivec2 c = ivec2(gl_FragCoord.xy);
            vec4 base0 = texelFetch(b0, c, 0);
            float w = clamp(texture(uMask, vUv).r * uStrength, 0.0, 1.0) * texelFetch(r0, c, 0).a;
            o0 = vec4(mix(base0.rgb, texelFetch(r0, c, 0).rgb, w), base0.a);
            vec4 h = texelFetch(b1, c, 0); o1 = vec4(mix(h.r, texelFetch(r1, c, 0).r, w), 0.0, 0.0, h.a);
            vec4 sp = texelFetch(b2, c, 0); o2 = vec4(mix(sp.rgb, texelFetch(r2, c, 0).rgb, w), sp.a);
            vec4 ss = texelFetch(b3, c, 0); o3 = vec4(mix(ss.rgb, texelFetch(r3, c, 0).rgb, w), ss.a);
            o4 = mix(texelFetch(b4, c, 0), texelFetch(r4, c, 0), w);
        }
        """;

    public const string PreviewVertex = """
        #version 330 core
        layout(location=0) in vec3 aPos;
        layout(location=1) in vec2 aUv;
        layout(location=2) in vec3 aNrm;
        uniform mat4 uMvp;
        out vec2 vUv; out vec3 vNrm; out vec3 vPos;
        void main() { vUv = aUv; vNrm = aNrm; vPos = aPos; gl_Position = uMvp * vec4(aPos, 1.0); }
        """;

    /// <summary>Preview: lit with the baked model-space normal map, or one channel shown flat.</summary>
    public const string PreviewFragment = """
        #version 330 core
        in vec2 vUv; in vec3 vNrm; in vec3 vPos;
        out vec4 frag;
        uniform sampler2D uAlbedo, uNormal, uSpec, uSss, uRfaos, uShell;
        uniform int uMode; uniform int uReady; uniform vec3 uLight; uniform vec3 uEye;
        uniform sampler2D uPaint; uniform int uHasPaint;
        uniform vec4 uBrush, uBrushMirror; // centre, radius (0 = hidden)
        vec3 shade();
        // the painted mask tinted over the surface, and the brush outline
        vec3 overlay(vec3 c) {
            if (uHasPaint == 1) c = mix(c, vec3(1.0, 0.15, 0.1), texture(uPaint, vUv).r * 0.6);
            for (int i = 0; i < 2; i++) {
                vec4 b = i == 0 ? uBrush : uBrushMirror;
                if (b.w <= 0.0) continue;
                float d = length(vPos - b.xyz) / b.w;
                float ring = 1.0 - smoothstep(0.0, 0.04, abs(d - 1.0));
                c = mix(c, vec3(1.0), ring * 0.9);
                c = mix(c, vec3(1.0), (1.0 - step(1.0, d)) * 0.08);
            }
            return c;
        }
        void main() { frag = vec4(overlay(shade()), 1.0); }
        vec3 shade() {
            if (uReady == 0) return vec3(0.6) * (0.4 + 0.6 * abs(dot(normalize(vNrm), uLight)));
            vec3 albedo = texture(uAlbedo, vUv).rgb;
            vec3 msn = texture(uNormal, vUv).rgb;
            vec3 n = normalize(msn.xzy * 2.0 - 1.0);
            float spec = texture(uSpec, vUv).r;
            vec3 sss = texture(uSss, vUv).rgb;
            if (uMode == 1) return albedo;
            if (uMode == 2) return msn;
            if (uMode == 3) return vec3(spec);
            if (uMode == 4) return sss;
            if (uMode == 5) return texture(uRfaos, vUv).rgb;
            if (uMode == 6) { vec4 sh = texture(uShell, vUv); return mix(vec3(0.05), sh.rgb, sh.a); } // fur strands (shell alpha) in fur colour
            vec3 v = normalize(uEye - vPos);
            float ndl = dot(n, uLight);
            float diffuse = max(ndl, 0.0);
            float wrap = max((ndl + 0.5) / 1.5, 0.0); // cheap subsurface wrap
            vec3 hv = normalize(uLight + v);
            float s = pow(max(dot(n, hv), 0.0), 40.0) * spec;
            vec3 colour = albedo * (0.25 + 0.75 * diffuse) + albedo * sss * (wrap - diffuse) * 0.6 + vec3(s);
            return colour;
        }
        """;
}
