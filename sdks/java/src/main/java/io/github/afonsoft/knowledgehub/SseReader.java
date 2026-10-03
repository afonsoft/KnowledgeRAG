package io.github.afonsoft.knowledgehub;

import com.fasterxml.jackson.core.type.TypeReference;
import com.fasterxml.jackson.databind.ObjectMapper;
import java.io.BufferedReader;
import java.io.IOException;
import java.io.InputStream;
import java.io.InputStreamReader;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.Iterator;
import java.util.List;
import java.util.Map;
import java.util.NoSuchElementException;

/**
 * Minimal Server-Sent-Events reader for the hub's REST streaming endpoints
 * ({@code POST /api/ask/stream}, {@code POST /api/agent/stream}). Parses
 * {@code event:} + {@code data:} frames and unwraps the wire envelope
 * {@code {"seq": n, "data": {...}}}.
 */
final class SseReader {

    private SseReader() {
    }

    record Frame(String event, Map<String, Object> data) {
    }

    static Iterator<Frame> read(InputStream stream, ObjectMapper mapper) throws IOException {
        var frames = new ArrayList<Frame>();
        try (var reader = new BufferedReader(new InputStreamReader(stream, StandardCharsets.UTF_8))) {
            String event = "message";
            var data = new StringBuilder();
            String line;
            while ((line = reader.readLine()) != null) {
                if (line.isEmpty()) {
                    if (data.length() > 0) {
                        frames.add(new Frame(event, unwrap(data.toString(), mapper)));
                    }
                    event = "message";
                    data.setLength(0);
                    continue;
                }
                if (line.startsWith("event:")) {
                    event = line.substring(6).trim();
                } else if (line.startsWith("data:")) {
                    if (data.length() > 0) {
                        data.append('\n');
                    }
                    data.append(line.substring(5).trim());
                }
                // ':' comment lines (keep-alives) are ignored.
            }
            if (data.length() > 0) {
                frames.add(new Frame(event, unwrap(data.toString(), mapper)));
            }
        }
        return frames.iterator();
    }

    /** Unwraps {@code {"seq": n, "data": {...}}} envelopes into the inner payload. */
    @SuppressWarnings("unchecked")
    private static Map<String, Object> unwrap(String json, ObjectMapper mapper) throws IOException {
        Map<String, Object> parsed = mapper.readValue(json, new TypeReference<>() {
        });
        Object inner = parsed.get("data");
        if (parsed.containsKey("seq") && inner instanceof Map) {
            return (Map<String, Object>) inner;
        }
        return parsed;
    }

    /** Drains a stream into all frames (used for eager iteration). */
    static List<Frame> readAll(InputStream stream, ObjectMapper mapper) throws IOException {
        var out = new ArrayList<Frame>();
        read(stream, mapper).forEachRemaining(out::add);
        return out;
    }

    static NoSuchElementException end() {
        return new NoSuchElementException("SSE stream exhausted");
    }
}
