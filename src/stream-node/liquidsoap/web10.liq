# Liquidsoap 2.4 exposes frame geometry below settings.frame.video.
# The stream container owns the isolated process tree as root; Liquidsoap has no
# public control endpoint and only its loopback supervisor can access its socket.
settings.init.allow_root := true
settings.frame.video.width := int_of_string(
  environment.get(default="1280", "WEB10_STREAM__WIDTH"),
  default=1280
)
settings.frame.video.height := int_of_string(
  environment.get(default="720", "WEB10_STREAM__HEIGHT"),
  default=720
)
settings.frame.video.framerate := int_of_string(
  environment.get(default="30", "WEB10_STREAM__FRAMERATE"),
  default=30
)

# The command interface is intentionally filesystem-only: the supervisor owns
# its permissions and is the sole client of this socket.
settings.server.telnet := false
settings.server.socket := true
settings.server.socket.path := "/run/web10/liquidsoap.sock"

display = environment.get(default=":99", "WEB10_STREAM__DISPLAY")
width = settings.frame.video.width()
height = settings.frame.video.height()
framerate = settings.frame.video.framerate()
bitrate_kbps = int_of_string(
  environment.get(default="192", "WEB10_STREAM__BITRATE_KBPS"),
  default=192
)
rtmp_url = environment.get("WEB10_STREAM__RTMP_URL")
rtmp_key = environment.get("WEB10_STREAM__RTMP_KEY")
rtmp_target = "#{rtmp_url}#{rtmp_key}"

web10_api_base = environment.get("WEB10_API__BASE_URL")
web10_callback_token = environment.get("WEB10_STREAM__CALLBACK_TOKEN")
web10_auth_header_file = "/run/web10/media-auth.header"
file.write(data="Authorization: Bearer #{web10_callback_token}\n", perms=0o600, web10_auth_header_file)

def web10media_protocol(~rlog, ~maxtime, arg) =
  ignore(rlog)
  ignore(maxtime)
  extname = file.extension(leading_dot=false, arg)
  queue_item_id = path.remove_extension(arg)
  media_url = "#{web10_api_base}/api/v0/stream-node/playback/#{queue_item_id}/media"
  process.uri(extname=extname, "curl -fsSL -H @#{web10_auth_header_file} -o $(output) #{media_url}")
end
protocol.add(
  "web10media",
  web10media_protocol,
  temporary=true,
  doc="Fetch the assigned track from the API over HTTP",
  syntax="web10media:<queueItemId>.<ext>"
)

# A CUE request always resolves the ordinary authenticated FLAC object first.
# process.uri gives FFmpeg a private local $(input) and a private $(output);
# this resolver therefore never writes a trimmed object into shared cache/storage.
def web10cue_protocol(~rlog, ~maxtime, arg) =
  ignore(maxtime)
  parts = string.split(separator=":", path.remove_extension(arg))
  extension = file.extension(leading_dot=false, arg)
  if list.length(parts) == 3 then
    let [queue_item_id, start_raw, duration_raw] = parts
    start_ms = int_of_string(start_raw, default=-1)
    duration_ms = int_of_string(duration_raw, default=-1)
    valid_uuid = r/^[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}$/.test(queue_item_id)
    if extension == "flac" and valid_uuid and start_ms >= 0 and duration_ms > 0 then
      start_seconds = string.float(decimal_places=3, float_of_int(start_ms) / 1000.)
      duration_seconds = string.float(decimal_places=3, float_of_int(duration_ms) / 1000.)
      source_uri = "web10media:#{queue_item_id}.flac"
      process.uri(
        extname="flac",
        uri=source_uri,
        "ffmpeg -hide_banner -loglevel error -i $(input) -ss #{start_seconds} -t #{duration_seconds} -map 0:a:0 -vn -sn -dn -c:a flac -y $(output)"
      )
    else
      rlog("Invalid web10cue request URI.")
      failwith("Invalid web10cue request URI.")
    end
  else
    rlog("Invalid web10cue request URI.")
    failwith("Invalid web10cue request URI.")
  end
end
protocol.add(
  "web10cue",
  web10cue_protocol,
  temporary=true,
  doc="Decode a bounded FLAC CUE segment from an assigned media object",
  syntax="web10cue:<queueItemId>:<cueStartMs>:<cueDurationMs>.flac"
)

# Liquidsoap 2.4's experimental external readers do not preserve pipe framing
# for rawvideo or AVI. Its native FFmpeg input captures the same X11 surface
# directly, with decoded YUV frames and no intermediary framing failure.
captured_video = input.ffmpeg(
  id="web10-video",
  format="x11grab",
  int_args=[("framerate", framerate), ("draw_mouse", 0)],
  string_args=[("video_size", "#{width}x#{height}")],
  display
)
video = mksafe(captured_video)

queue = request.queue(id="web10", prefetch=1)
audio = fallback(track_sensitive=false, [queue, blank()])
stream = source.mux.video(video=video, audio)

# Only the local fencing identity is exposed to callback handlers. Title and
# artist remain inside the media pipeline and are never sent to loopback.
# Annotated metadata is string-only. Build the fixed three-field callback
# object directly so claimAttempt remains an API integer, not a JSON string.
def assignment_callback_body(track_metadata) =
  queue_item_id = list.assoc("web10_queue_item_id", track_metadata)
  claim_owner = list.assoc("web10_claim_owner", track_metadata)
  claim_attempt = list.assoc("web10_claim_attempt", track_metadata)
  "{\"queueItemId\":\"#{queue_item_id}\",\"claimOwner\":\"#{claim_owner}\",\"claimAttempt\":#{claim_attempt}}"
end

def post_assignment_callback(path, track_metadata) =
  response = http.post(
    headers=[("Content-Type", "application/json")],
    data=assignment_callback_body(track_metadata),
    timeout=2.0,
    "http://127.0.0.1:18080/callbacks/#{path}"
  )
  if response.status_code != 204 then
    shutdown(code=1)
  end
end

queue.on_track(
  synchronous=false,
  fun (track_metadata) -> post_assignment_callback("started", track_metadata)
)
queue.on_position(
  position=0.0,
  remaining=true,
  allow_partial=true,
  synchronous=false,
  fun (_, track_metadata) -> post_assignment_callback("ended", track_metadata)
)

# Liquidsoap's global error monitor preserves the output error domain without
# exposing transport details to logs or loopback callbacks.
output_failure_notified = ref(false)
def post_output_failure() =
  if not output_failure_notified() then
    output_failure_notified := true
    response = http.post(
      headers=[("Content-Type", "application/json")],
      data="{}",
      timeout=2.0,
      "http://127.0.0.1:18080/callbacks/output-failed"
    )
  end
end
error.on_error(
  fun (err) ->
    if err.kind == "ffmpeg" then
      post_output_failure()
    end
)

gop = 2 * framerate
rtmp_encoder = %ffmpeg(
  format="flv",
  %audio(codec="aac", samplerate=48000, b="#{bitrate_kbps}k"),
  %video(
    codec="libx264",
    pixel_format="yuv420p",
    b="2500k",
    preset="veryfast",
    framerate=framerate,
    g=gop
  )
)
rtmp = output.url(
  id="web10-rtmp",
  register_telnet=false,
  self_sync=true,
  restart_delay=null,
  url=rtmp_target,
  rtmp_encoder,
  stream
)

# These explicit commands avoid relying on undocumented output-status commands.
def bool_response(value) =
  if value then
    "true"
  else
    "false"
  end
end
server.register(
  namespace="web10-video",
  description="Return whether the X11 raw-video input is ready.",
  usage="is_ready",
  "is_ready",
  fun (_) -> bool_response(captured_video.is_ready())
)
server.register(
  namespace="web10-rtmp",
  description="Return whether the RTMP output has started.",
  usage="is_started",
  "is_started",
  fun (_) -> bool_response(rtmp.is_started())
)
