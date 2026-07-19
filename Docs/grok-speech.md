Grok Text-to-speech



# Text-to-Speech via REST API
# export XAI_API_KEY="xai-..."

curl -X POST "https://api.x.ai/v1/tts" \
  -H "Authorization: Bearer $XAI_API_KEY" \
  -H "Content-Type: application/json" \
  -d '{
    "text": "Hello! This is a text-to-speech demo.",
    "voice_id": "eve",
    "output_format": {
      "codec": "mp3",
      "sample_rate": 44100,
      "bit_rate": 128000
    },
    "language": "fr"
  }' \
  --output output.mp3

echo "Saved to output.mp3"

Effects (could be fun for robot):

Instant:
 [laugh]
 [chuckle]
 [hum-tune]
 [giggle]
 [cry]
 [tongue-click]
 [lip-smack]
 [breath]
 [inhale]
 [exhale]
 [sigh]
 [pause]
 [long-pause]
 
Wrapping:
 <soft> ... </soft>
 <whisper> ... </whisper>
 <singing> ...  </singing>
 <laugh-speak> .. </laugh-speak>
 <emphasis> ... </emphasis>
 
# Language 
Supports multiple
I like the fr for aslight french accent.  