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
  @location(1) unit: vec2<f32>,
  @location(2) selected: f32,
  @location(3) shape: f32,
};

@vertex
fn vs(
  @builtin(vertex_index) vertexIndex: u32,
  @location(0) world: vec2<f32>,
  @location(1) color: vec4<f32>,
  @location(2) size: f32,
  @location(3) flags: f32,
  @location(4) halfSize: vec2<f32>,
  @location(5) shape: f32
) -> VertexOut {
  var corners = array<vec2<f32>, 6>(
    vec2<f32>(-1.0, -1.0),
    vec2<f32>(1.0, -1.0),
    vec2<f32>(-1.0, 1.0),
    vec2<f32>(-1.0, 1.0),
    vec2<f32>(1.0, -1.0),
    vec2<f32>(1.0, 1.0)
  );

  let unit = corners[vertexIndex];
  let selected = select(0.0, 1.0, flags > 0.5);
  let circleHalfSize = vec2<f32>(size + selected * 4.0, size + selected * 4.0);
  let recordHalfSize = halfSize + vec2<f32>(selected * 4.0, selected * 4.0);
  let scaledHalfSize = select(circleHalfSize, recordHalfSize, shape > 0.5) * uniforms.scale;
  let css = world * uniforms.scale + uniforms.offset + unit * scaledHalfSize;
  let screen = css * uniforms.pixelRatio;
  let clip = vec2<f32>(
    screen.x / uniforms.viewport.x * 2.0 - 1.0,
    1.0 - screen.y / uniforms.viewport.y * 2.0
  );

  var out: VertexOut;
  out.position = vec4<f32>(clip, 0.0, 1.0);
  out.color = color;
  out.unit = unit;
  out.selected = selected;
  out.shape = shape;
  return out;
}

@fragment
fn fs(in: VertexOut) -> @location(0) vec4<f32> {
  if (in.shape > 0.5) {
    let edge = max(abs(in.unit.x), abs(in.unit.y));
    let alpha = smoothstep(1.0, 0.94, edge);
    if (in.selected > 0.5 && edge > 0.84) {
      return vec4<f32>(1.0, 0.78, 0.24, alpha);
    }

    if (edge > 0.88) {
      return vec4<f32>(in.color.rgb, in.color.a * alpha);
    }

    return vec4<f32>(1.0, 1.0, 1.0, 0.98);
  }

  let distance = length(in.unit);
  if (distance > 1.0) {
    discard;
  }

  let alpha = smoothstep(1.0, 0.78, distance);
  if (in.selected > 0.5 && distance > 0.70) {
    return vec4<f32>(1.0, 0.78, 0.24, alpha);
  }

  if (distance > 0.82) {
    return vec4<f32>(in.color.rgb, in.color.a * alpha);
  }

  return vec4<f32>(1.0, 1.0, 1.0, 0.98);
}
