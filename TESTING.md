## Video Download Manager - Test Instructions

### Fixed Issues:
1. **Button Disabled Problem**: Fixed by adding proper PropertyChanged notification for `CanAddItem`
2. **Format Options**: Added comprehensive video and audio format options
3. **Queue Display**: Fixed download queue not showing added items

### New Format Options Available:

#### Video Formats:
- **Best Video (4K/1080p/720p)** - Automatically selects best available quality
- **4K Video (2160p)** - Ultra HD quality
- **1080p Video** - Full HD quality  
- **720p Video** - HD quality
- **480p Video** - Standard definition
- **360p Video** - Mobile quality

#### Audio-Only Formats:
- **Audio Only - Best Quality** - Highest quality audio available
- **Audio Only - MP3 320kbps** - High quality MP3
- **Audio Only - MP3 256kbps** - Good quality MP3
- **Audio Only - MP3 128kbps** - Standard quality MP3
- **Audio Only - AAC Best** - High quality AAC format
- **Audio Only - FLAC** - Lossless audio quality
- **Audio Only - OGG** - Open source audio format

#### Combined Formats:
- **Video + Audio - Best** - Best video and audio combined
- **Video + Audio - 1080p + Best Audio** - 1080p video with best audio
- **Video + Audio - 720p + Best Audio** - 720p video with best audio

### How to Test:
1. Enter any valid video URL from supported platforms
2. Select desired format from dropdown
3. Click "Add to Queue" or "Add & Start"
4. Items should appear in the download queue
5. Buttons should be enabled when URL is entered

### Requirements:
- yt-dlp must be installed and available in PATH
- For audio extraction, ffmpeg is required

### Example URLs for Testing:
- Any video platform URL supported by yt-dlp
- Test with various video hosting platforms
