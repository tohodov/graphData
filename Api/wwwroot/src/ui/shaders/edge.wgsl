struct Uniforms {
  viewport: vec2<f32>,
  scale: f32,
  pixelRatio: f32,
  offset: vec2<f32>,
  _pad: vec2<f32>,
};

@group(0) @binding(0) var<uniform> uniforms: Uniforms;

struct VertexOut {
  @builtin(position) position: vec4<f32>,
  @location(0) color: vec4<f32>,
};

@vertex
fn vs(
  @location(0) world: vec2<f32>,
  @location(1) color: vec4<f32>
) -> VertexOut {
  let css = world * uniforms.scale + uniforms.offset;
  let screen = css * uniforms.pixelRatio;
  let clip = vec2<f32>(
    screen.x / uniforms.viewport.x * 2.0 - 1.0,
    1.0 - screen.y / uniforms.viewport.y * 2.0
  );

  var out: VertexOut;
  out.position = vec4<f32>(clip, 0.0, 1.0);
  out.color = color;
  return out;
}

@fragment
fn fs(in: VertexOut) -> @location(0) vec4<f32> {
  return in.color;
}
