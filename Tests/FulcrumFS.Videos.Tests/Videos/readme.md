To figure out what each file is meant to be, view `FFprobeUtilsTests.cs`, as it lists all of the files & their expected FFprobe info:
- The files named `videoN.ext` where `N` is an integer are synthetic videos used to test a bunch of cases.
- The file `bbb_sunflower_1080p_60fps_normal-25s.mp4` is the first 25 seconds (to reduce size & processing time) of big buck bunny, licensed under [CC BY 3.0](https://creativecommons.org/licenses/by/3.0/), as per https://peach.blender.org/about/, which is (c) copyright 2008, Blender Foundation / www.bigbuckbunny.org, which was generated with the command `ffmpeg -i bbb_sunflower_1080p_60fps_normal.mp4 -t 1 -c copy -map v:0 -map a:0 -y bbb_sunflower_1080p_60fps_normal-25s.mp4` with the file from https://download.blender.org/demo/movies/BBB/ (linked from https://peach.blender.org/download/).
- Similarly, the file `bbb_sunflower_1080p_60fps_normal-1s.mp4` was generated with `ffmpeg -i bbb_sunflower_1080p_60fps_normal.mp4 -t 1 -c copy -map v:0 -map a:0 -y bbb_sunflower_1080p_60fps_normal-1s.mp4` (under the same license).
- The files `Y0__auYqGXY-20s.mp4`, `Y0__auYqGXY-5s.mp4`, `Y0__auYqGXY-1s-low.mp4` and `Y0__auYqGXY-1s-high.mp4` are based on https://www.youtube.com/watch?v=Y0__auYqGXY, which is released under [CC BY](https://support.google.com/youtube/answer/2797468) as per their description. Title of the work: "Nature is breathtaking @Vindsvept #relaxing", Author of the work: [breathe.](https://www.youtube.com/@LiminalLofi7). They were modified using ffmpeg using the following commands: `ffmpeg -i original.mp4 -an -c:v libx265 -crf 16 -t 20s -y -tag:v hvc1 -y Y0__auYqGXY-20s.mp4`, `ffmpeg -ss 55 -i original.mp4 -an -c:v libx265 -crf 16 -t 5s -tag hvc1 -y Y0__auYqGXY-5s.mp4`, `ffmpeg -ss 55 -i original.mp4 -an -c:v libx265 -crf 50 -t 1s -tag hvc1 -y Y0__auYqGXY-1s-low.mp4`, `ffmpeg -ss 55 -i original.mp4 -an -c:v libx265 -crf 1 -t 1s -tag hvc1 -y Y0__auYqGXY-1s-high.mp4`.

Currently the following sets of cases test the following functionality of our ffprobe code:
- Media container formats: 1-8
- Video codecs: 1, 4, 6-12, 15
- Audio codecs: 1, 4, 6-7, 13-14
- Subtitle codecs: 16-20, 169-170
- Subtitle language / title: 21-29
- Pixel format / color range combinations: 1, 30-52
- Thumbnail detection: 53-54
- H.264/H.265 video profiles: 1, 10, 30-32, 35, 41, 50, 55-56, 84-88
- Audio channel counts / layouts: 57-66, 150-157
- Audio sample rates: 1, 67-83
- Video fps: 1, 89-104, 167
- Video resolution: 1, 8, 105-110, 136-149
- Pixel shapes (SAR): 1, 111, 166
- Interlacing: 1, 112-113
- HDR: 114
- H.264 levels: 1 (level 1.0), 115 (level 1.1), 116 (level 1.2), 117 (level 1.3), 118 (level 2.0), 119 (level 2.1), 120 (level 2.2), 121 (level 3.0), 122 (level 3.1), 123 (level 3.2), 124 (level 4.0), 125 (level 4.1), 126 (level 4.2), 127 (level 5.0), 128 (level 5.1), 129 (level 5.2), 130 (level 6.0), 131 (level 6.1), 132 (level 6.2)
- Many Streams: 133
- Unrecognised Streams: 134-135, 159
- Incorrect duration: 158, 196
- One stream only: 159-161
- Metadata other than on subtitle streams: 162-163
- HEVC Tags: 10, 164-165
- Zero streams: 168
- Over/under-sized streams: 171-174
- Invalid (only when validating) file: 175
- Misc. sizes for unit testing: 176-187, 197-200
- Misc. files for SelectSmallest unit testing: 188-195

The following files are also explicitly used for tests in `Tests.cs`: 1-21, 30-54, 57, 60, 67-82, 95, 100-103, 111, 114, 133-136, 143, 158-164, 166-200

Commands to generate the synthetic videos:
1. `ffmpeg -f lavfi -i color=c=green:s=128x72:d=1:r=30 -f lavfi -i sine=frequency=440:duration=1 -shortest -c:v libx264 -c:a libfdk_aac -ar 44100 -ac 2 -pix_fmt yuv420p -color_range pc -profile:v main -y video1.mp4`
2. `ffmpeg -f lavfi -i color=c=green:s=128x72:d=1:r=30 -f lavfi -i sine=frequency=440:duration=1 -shortest -c:v libx264 -c:a libfdk_aac -ar 44100 -ac 2 -pix_fmt yuv420p -color_range pc -profile:v main -y video2.mkv`
3. `ffmpeg -f lavfi -i color=c=green:s=128x72:d=1:r=30 -f lavfi -i sine=frequency=440:duration=1 -shortest -c:v libx264 -c:a libfdk_aac -ar 44100 -ac 2 -pix_fmt yuv420p -color_range pc -profile:v main -y video3.mov`
4. `ffmpeg -f lavfi -i color=c=green:s=128x72:d=1:r=30 -f lavfi -i sine=frequency=440:duration=1 -shortest -c:v vp9 -c:a libopus -ar 48000 -ac 2 -pix_fmt yuv420p -color_range pc -y video4.webm`
5. `ffmpeg -f lavfi -i color=c=green:s=128x72:d=1:r=30 -f lavfi -i sine=frequency=440:duration=1 -shortest -c:v libx264 -c:a libfdk_aac -ar 44100 -ac 2 -pix_fmt yuv420p -color_range pc -profile:v main -y video5.avi`
6. `ffmpeg -f lavfi -i color=c=green:s=128x72:d=1:r=30 -f lavfi -i sine=frequency=440:duration=1 -shortest -c:v mpeg2video -c:a mp3 -ar 44100 -ac 2 -pix_fmt yuv420p -y video6.ts`
7. `ffmpeg -f lavfi -i color=c=green:s=128x72:d=99:r=60 -f lavfi -i sine=frequency=440:duration=1 -shortest -c:v mpeg1video -c:a mp2 -ar 44100 -ac 2 -pix_fmt yuv420p -y video7.mpeg`
8. `ffmpeg -f lavfi -i color=c=green:s=128x96:d=1:r=30 -f lavfi -i sine=frequency=440:duration=1 -shortest -c:v h263 -c:a libfdk_aac -ar 44100 -ac 2 -pix_fmt yuv420p -color_range pc -y video8.3gp`
9. `ffmpeg -f lavfi -i color=c=green:s=128x72:d=1:r=30 -f lavfi -i sine=frequency=440:duration=1 -shortest -c:v h263p -c:a libfdk_aac -ar 44100 -ac 2 -pix_fmt yuv420p -color_range pc -y video9.avi`
10. `ffmpeg -f lavfi -i color=c=green:s=128x72:d=1:r=30 -f lavfi -i sine=frequency=440:duration=1 -shortest -c:v libx265 -c:a libfdk_aac -ar 44100 -ac 2 -pix_fmt yuv420p -color_range pc -y video10.mp4`
11. `ffmpeg -f lavfi -i color=c=green:s=128x72:d=1:r=30 -f lavfi -i sine=frequency=440:duration=1 -shortest -c:v vp8 -c:a libfdk_aac -ar 44100 -ac 2 -pix_fmt yuv420p -color_range pc -y video11.mkv`
12. `ffmpeg -f lavfi -i color=c=green:s=128x72:d=1:r=30 -f lavfi -i sine=frequency=440:duration=1 -shortest -c:v av1 -c:a libfdk_aac -ar 44100 -ac 2 -pix_fmt yuv420p -color_range pc -y video12.mp4`
13. `ffmpeg -f lavfi -i color=c=green:s=128x72:d=1:r=30 -f lavfi -i sine=frequency=440:duration=1 -shortest -c:v libx264 -c:a libfdk_aac -ar 44100 -ac 2 -pix_fmt yuv420p -color_range pc -profile:v main -profile:a aac_he -y video13.mp4`
14. `ffmpeg -f lavfi -i color=c=green:s=128x72:d=1:r=30 -f lavfi -i sine=frequency=440:duration=1 -shortest -c:v libx264 -c:a libvorbis -ar 44100 -ac 2 -pix_fmt yuv420p -color_range pc -profile:v main -y video14.mp4`
15. `ffmpeg -i video1.mp4 -y video15.yuv`, `uvg266 --input video15.yuv --input-res 128x72 --input-fps 30 --output video15.vvc`, `ffmpeg -i video15.vvc -i video1.mp4 -map 0 -map 1:a -c copy -y video15.mp4`, `rm video15.yuv`, `rm video15.vvc`
16. `ffmpeg -i video1.mp4 -i test_subtitles_1.srt -map 0 -map 1 -c copy -c:s srt -y video16.mkv`
17. `ffmpeg -i video1.mp4 -i test_subtitles_1.srt -map 0 -map 1 -c copy -c:s ssa -y video17.mkv`
18. `ffmpeg -i video1.mp4 -i test_subtitles_1.srt -map 0 -map 1 -c copy -c:s ass -y video18.mkv`
19. `ffmpeg -i video1.mp4 -i test_subtitles_1.srt -map 0 -map 1 -c copy -c:s webvtt -y video19.mkv`
20. `ffmpeg -i video1.mp4 -i test_subtitles_1.srt -map 0 -map 1 -c copy -c:s mov_text -y video20.mp4`
21. `ffmpeg -i video1.mp4 -i test_subtitles_1.srt -map 0 -map 1 -c copy -c:s mov_text -metadata:s:s:0 language="eng" -y video21.mp4`
22. `ffmpeg -i video1.mp4 -i test_subtitles_1.srt -map 0 -map 1 -c copy -c:s srt -metadata:s:s:0 title="Test Title" -y video22.mkv`
23. `ffmpeg -i video1.mp4 -i test_subtitles_1.srt -map 0 -map 1 -c copy -c:s ssa -metadata:s:s:0 title="Test Title" -y video23.mkv`
24. `ffmpeg -i video1.mp4 -i test_subtitles_1.srt -map 0 -map 1 -c copy -c:s ass -metadata:s:s:0 title="Test Title" -y video24.mkv`
25. `ffmpeg -i video1.mp4 -i test_subtitles_1.srt -map 0 -map 1 -c copy -c:s webvtt -metadata:s:s:0 title="Test Title" -y video25.mkv`
26. `ffmpeg -i video1.mp4 -i test_subtitles_1.srt -map 0 -map 1 -c copy -c:s srt -metadata:s:s:0 language="eng" -y video26.mkv`
27. `ffmpeg -i video1.mp4 -i test_subtitles_1.srt -map 0 -map 1 -c copy -c:s ssa -metadata:s:s:0 language="eng" -y video27.mkv`
28. `ffmpeg -i video1.mp4 -i test_subtitles_1.srt -map 0 -map 1 -c copy -c:s ass -metadata:s:s:0 language="eng" -y video28.mkv`
29. `ffmpeg -i video1.mp4 -i test_subtitles_1.srt -map 0 -map 1 -c copy -c:s webvtt -metadata:s:s:0 language="eng" -y video29.mkv`
30. `ffmpeg -f lavfi -i color=c=green:s=128x72:d=1:r=30 -i video1.mp4 -map 0 -map 1:a -c copy -c:v libx264 -pix_fmt yuv422p -color_range pc -y video30.mp4`
31. `ffmpeg -f lavfi -i color=c=green:s=128x72:d=1:r=30 -i video1.mp4 -map 0 -map 1:a -c copy -c:v libx264 -pix_fmt yuv444p -color_range pc -y video31.mp4`
32. `ffmpeg -f lavfi -i color=c=green:s=128x72:d=1:r=30 -i video1.mp4 -map 0 -map 1:a -c copy -c:v libx264 -pix_fmt yuv420p10le -color_range pc -y video32.mp4`
33. `ffmpeg -f lavfi -i color=c=green:s=128x72:d=1:r=30 -i video1.mp4 -map 0 -map 1:a -c copy -c:v libx264 -pix_fmt yuv422p10le -color_range pc -y video33.mp4`
34. `ffmpeg -f lavfi -i color=c=green:s=128x72:d=1:r=30 -i video1.mp4 -map 0 -map 1:a -c copy -c:v libx264 -pix_fmt yuv444p10le -color_range pc -y video34.mp4`
35. `ffmpeg -f lavfi -i color=c=green:s=128x72:d=1:r=30 -i video1.mp4 -map 0 -map 1:a -c copy -c:v libx264 -pix_fmt yuv420p -color_range tv -y video35.mp4`
36. `ffmpeg -f lavfi -i color=c=green:s=128x72:d=1:r=30 -i video1.mp4 -map 0 -map 1:a -c copy -c:v libx264 -pix_fmt yuv422p -color_range tv -y video36.mp4`
37. `ffmpeg -f lavfi -i color=c=green:s=128x72:d=1:r=30 -i video1.mp4 -map 0 -map 1:a -c copy -c:v libx264 -pix_fmt yuv444p -color_range tv -y video37.mp4`
38. `ffmpeg -f lavfi -i color=c=green:s=128x72:d=1:r=30 -i video1.mp4 -map 0 -map 1:a -c copy -c:v libx264 -pix_fmt yuv420p10le -color_range tv -y video38.mp4`
39. `ffmpeg -f lavfi -i color=c=green:s=128x72:d=1:r=30 -i video1.mp4 -map 0 -map 1:a -c copy -c:v libx264 -pix_fmt yuv422p10le -color_range tv -y video39.mp4`
40. `ffmpeg -f lavfi -i color=c=green:s=128x72:d=1:r=30 -i video1.mp4 -map 0 -map 1:a -c copy -c:v libx264 -pix_fmt yuv444p10le -color_range tv -y video40.mp4`
41. `ffmpeg -f lavfi -i color=c=green:s=128x72:d=1:r=30 -i video1.mp4 -map 0 -map 1:a -c copy -c:v libx265 -pix_fmt gbrp -color_range pc -y video41.mp4`
42. `ffmpeg -f lavfi -i color=c=green:s=128x72:d=1:r=30 -i video1.mp4 -map 0 -map 1:a -c copy -c:v libx265 -pix_fmt gbrp10le -color_range pc -y video42.mp4`
43. `ffmpeg -f lavfi -i color=c=green:s=128x72:d=1:r=30 -i video1.mp4 -map 0 -map 1:a -c copy -c:v libx265 -pix_fmt yuv420p12le -color_range pc -y video43.mp4`
44. `ffmpeg -f lavfi -i color=c=green:s=128x72:d=1:r=30 -i video1.mp4 -map 0 -map 1:a -c copy -c:v libx265 -pix_fmt yuv422p12le -color_range pc -y video44.mp4`
45. `ffmpeg -f lavfi -i color=c=green:s=128x72:d=1:r=30 -i video1.mp4 -map 0 -map 1:a -c copy -c:v libx265 -pix_fmt yuv444p12le -color_range pc -y video45.mp4`
46. `ffmpeg -f lavfi -i color=c=green:s=128x72:d=1:r=30 -i video1.mp4 -map 0 -map 1:a -c copy -c:v libx265 -pix_fmt gbrp12le -color_range pc -y video46.mp4`
47. `ffmpeg -f lavfi -i color=c=green:s=128x72:d=1:r=30 -i video1.mp4 -map 0 -map 1:a -c copy -c:v libx265 -pix_fmt yuv420p12le -color_range tv -y video47.mp4`
48. `ffmpeg -f lavfi -i color=c=green:s=128x72:d=1:r=30 -i video1.mp4 -map 0 -map 1:a -c copy -c:v libx265 -pix_fmt yuv422p12le -color_range tv -y video48.mp4`
49. `ffmpeg -f lavfi -i color=c=green:s=128x72:d=1:r=30 -i video1.mp4 -map 0 -map 1:a -c copy -c:v libx265 -pix_fmt yuv444p12le -color_range tv -y video49.mp4`
50. `ffmpeg -f lavfi -i color=c=green:s=128x72:d=1:r=30 -i video1.mp4 -map 0 -map 1:a -c copy -c:v libx265 -pix_fmt yuv420p10le -color_range pc -y video50.mp4`
51. `ffmpeg -loop 1 -i test_image_1.png -i video1.mp4 -t 1 -r 30 -map 0 -map 1:a -c:a libopus -c:v libvpx-vp9 -pix_fmt yuva420p -color_range pc -ar 48000 -y video51.webm`
52. `ffmpeg -loop 1 -i test_image_1.png -i video1.mp4 -t 1 -r 30 -map 0 -map 1:a -c:a libopus -c:v libvpx-vp9 -pix_fmt yuva420p -color_range tv -ar 48000 -y video52.webm`
53. `ffmpeg -i video1.mp4 -i test_image_1.png -map 0 -map 1 -disposition:v:1 attached_pic -c copy -y video53.mp4`
54. `ffmpeg -i video1.mp4 -i test_image_1.png -map 0 -map 1 -disposition:v:1 attached_pic -c copy -c:v:1 mjpeg -y video54.mp4`
55. `ffmpeg -i video1.mp4 -c copy -c:v libx264 -profile:v baseline -y video55.mp4`
56. `ffmpeg -i video1.mp4 -c copy -c:v libx264 -profile:v high -y video56.mp4`
57. `ffmpeg -i video1.mp4 -ac 1 -channel_layout mono -c:v copy -y video57.mp4`
58. `ffmpeg -i video1.mp4 -ac 2 -channel_layout stereo -c:v copy -y video58.mp4`
59. `ffmpeg -i video1.mp4 -ac 4 -channel_layout quad -c:v copy -y video59.mp4`
60. `ffmpeg -i video1.mp4 -ac 6 -channel_layout 5.1 -c:v copy -y video60.mp4`
61. `ffmpeg -i video1.mp4 -ac 8 -channel_layout 7.1 -c:v copy -y video61.mp4`
62. `ffmpeg -i video1.mp4 -ac 3 -channel_layout 2.1 -c:v copy -y video62.mp4`
63. `ffmpeg -i video1.mp4 -ac 4 -channel_layout 3.1 -c:v copy -y video63.mp4`
64. `ffmpeg -i video1.mp4 -ac 5 -channel_layout 4.1 -c:v copy -y video64.mp4`
65. `ffmpeg -i video1.mp4 -ac 7 -channel_layout 6.1 -c:v copy -y video65.mp4`
66. `ffmpeg -i video1.mp4 -ac 16 -channel_layout "hexadecagonal" -c:v copy -codec:a aac -y video66.mp4`
67. `ffmpeg -i video1.mp4 -ar 8000 -c:v copy -c:a libfdk_aac -y video67.mp4`
68. `ffmpeg -i video1.mp4 -ar 11025 -c:v copy -c:a libfdk_aac -y video68.mp4`
69. `ffmpeg -i video1.mp4 -ar 12000 -c:v copy -c:a libfdk_aac -y video69.mp4`
70. `ffmpeg -i video1.mp4 -ar 16000 -c:v copy -c:a libfdk_aac -y video70.mp4`
71. `ffmpeg -i video1.mp4 -ar 22050 -c:v copy -c:a libfdk_aac -y video71.mp4`
72. `ffmpeg -i video1.mp4 -ar 24000 -c:v copy -c:a libfdk_aac -y video72.mp4`
73. `ffmpeg -i video1.mp4 -ar 32000 -c:v copy -c:a libfdk_aac -y video73.mp4`
74. `ffmpeg -i video1.mp4 -ar 48000 -c:v copy -c:a libfdk_aac -y video74.mp4`
75. `ffmpeg -i video1.mp4 -ar 64000 -c:v copy -c:a libfdk_aac -y video75.mp4`
76. `ffmpeg -i video1.mp4 -ar 88200 -c:v copy -c:a libfdk_aac -y video76.mp4`
77. `ffmpeg -i video1.mp4 -ar 96000 -c:v copy -c:a libfdk_aac -y video77.mp4`
78. `ffmpeg -i video1.mp4 -ar 192000 -c:v copy -c:a libvorbis -y video78.mp4`
79. `ffmpeg -i video1.mp4 -ar 200000 -c:v copy -c:a libvorbis -y video79.mp4`
80. `ffmpeg -i video1.mp4 -ar 4000 -c:v copy -c:a libvorbis -y video80.mp4`
81. `ffmpeg -i video1.mp4 -ar 22 -c:v copy -c:a libvorbis -y video81.mp4`
82. `ffmpeg -i video1.mp4 -ar 45000 -c:v copy -c:a libvorbis -y video82.mp4`
83. `ffmpeg -f lavfi -i color=c=green:s=128x72:d=30:r=30 -f lavfi -i sine=frequency=0.5:duration=30 -shortest -c:v libx264 -c:a libvorbis -ar 1 -ac 2 -pix_fmt yuv420p -color_range pc -profile:v main -y video83.mp4`
84. `ffmpeg -f lavfi -i color=c=green:s=128x72:d=1:r=30 -f lavfi -i sine=frequency=440:duration=1 -shortest -c:v libx264 -c:a libfdk_aac -ar 44100 -ac 2 -pix_fmt yuv422p10le -color_range pc -profile:v high422 -g 1 -y video84.mp4`
85. `ffmpeg -f lavfi -i color=c=green:s=128x72:d=1:r=30 -f lavfi -i sine=frequency=440:duration=1 -shortest -c:v libx264 -c:a libfdk_aac -ar 44100 -ac 2 -pix_fmt yuv444p10le -color_range pc -profile:v high444 -g 1 -y video85.mp4`
86. `ffmpeg -f lavfi -i color=c=green:s=128x72:d=1:r=30 -f lavfi -i sine=frequency=440:duration=1 -shortest -c:v libx264 -c:a libfdk_aac -ar 44100 -ac 2 -pix_fmt yuv420p10le -color_range pc -profile:v high10 -g 1 -y video86.mp4`
87. `ffmpeg -f lavfi -i color=c=green:s=128x72:d=1:r=30 -f lavfi -i sine=frequency=440:duration=1 -shortest -c:v libx265 -c:a libfdk_aac -ar 44100 -ac 2 -pix_fmt yuv420p -color_range pc -profile:v main-intra -g 1 -y video87.mp4`
88. `ffmpeg -f lavfi -i color=c=green:s=128x72:d=1:r=30 -f lavfi -i sine=frequency=440:duration=1 -shortest -c:v libx265 -c:a libfdk_aac -ar 44100 -ac 2 -pix_fmt yuv420p -color_range pc -profile:v msp -y video88.mp4`
89. `ffmpeg -f lavfi -i color=c=green:s=128x72:d=1:r=30000/1001 -f lavfi -i sine=frequency=440:duration=1 -shortest -c:v libx264 -c:a libfdk_aac -ar 44100 -ac 2 -pix_fmt yuv420p -color_range pc -profile:v main -y video89.mp4`
90. `ffmpeg -f lavfi -i color=c=green:s=128x72:d=1:r=60000/1001 -f lavfi -i sine=frequency=440:duration=1 -shortest -c:v libx264 -c:a libfdk_aac -ar 44100 -ac 2 -pix_fmt yuv420p -color_range pc -profile:v main -y video90.mp4`
91. `ffmpeg -f lavfi -i color=c=green:s=128x72:d=1:r=60 -f lavfi -i sine=frequency=440:duration=1 -shortest -c:v libx264 -c:a libfdk_aac -ar 44100 -ac 2 -pix_fmt yuv420p -color_range pc -profile:v main -y video91.mp4`
92. `ffmpeg -f lavfi -i color=c=green:s=128x72:d=1:r=24000/1001 -f lavfi -i sine=frequency=440:duration=1 -shortest -c:v libx264 -c:a libfdk_aac -ar 44100 -ac 2 -pix_fmt yuv420p -color_range pc -profile:v main -y video92.mp4`
93. `ffmpeg -f lavfi -i color=c=green:s=128x72:d=1:r=24 -f lavfi -i sine=frequency=440:duration=1 -shortest -c:v libx264 -c:a libfdk_aac -ar 44100 -ac 2 -pix_fmt yuv420p -color_range pc -profile:v main -y video93.mp4`
94. `ffmpeg -f lavfi -i color=c=green:s=128x72:d=1:r=25 -f lavfi -i sine=frequency=440:duration=1 -shortest -c:v libx264 -c:a libfdk_aac -ar 44100 -ac 2 -pix_fmt yuv420p -color_range pc -profile:v main -y video94.mp4`
95. `ffmpeg -f lavfi -i color=c=green:s=128x72:d=1:r=48000/1001 -f lavfi -i sine=frequency=440:duration=1 -shortest -c:v libx264 -c:a libfdk_aac -ar 44100 -ac 2 -pix_fmt yuv420p -color_range pc -profile:v main -y video95.mp4`
96. `ffmpeg -f lavfi -i color=c=green:s=128x72:d=1:r=48 -f lavfi -i sine=frequency=440:duration=1 -shortest -c:v libx264 -c:a libfdk_aac -ar 44100 -ac 2 -pix_fmt yuv420p -color_range pc -profile:v main -y video96.mp4`
97. `ffmpeg -f lavfi -i color=c=green:s=128x72:d=1:r=50 -f lavfi -i sine=frequency=440:duration=1 -shortest -c:v libx264 -c:a libfdk_aac -ar 44100 -ac 2 -pix_fmt yuv420p -color_range pc -profile:v main -y video97.mp4`
98. `ffmpeg -f lavfi -i color=c=green:s=128x72:d=1:r=120 -f lavfi -i sine=frequency=440:duration=1 -shortest -c:v libx264 -c:a libfdk_aac -ar 44100 -ac 2 -pix_fmt yuv420p -color_range pc -profile:v main -y video98.mp4`
99. `ffmpeg -f lavfi -i color=c=green:s=128x72:d=1:r=240 -f lavfi -i sine=frequency=440:duration=1 -shortest -c:v libx264 -c:a libfdk_aac -ar 44100 -ac 2 -pix_fmt yuv420p -color_range pc -profile:v main -y video99.mp4`
100. `ffmpeg -f lavfi -i color=c=green:s=128x72:d=1:r=300 -f lavfi -i sine=frequency=440:duration=1 -shortest -c:v libx264 -c:a libfdk_aac -ar 44100 -ac 2 -pix_fmt yuv420p -color_range pc -profile:v main -y video100.mp4`
101. `ffmpeg -f lavfi -i color=c=green:s=128x72:d=1:r=299999/1000 -f lavfi -i sine=frequency=440:duration=1 -shortest -c:v libx264 -c:a libfdk_aac -ar 44100 -ac 2 -pix_fmt yuv420p -color_range pc -profile:v main -y video101.mp4`
102. `ffmpeg -f lavfi -i color=c=green:s=128x72:d=1:r=1000573/4001 -f lavfi -i sine=frequency=440:duration=1 -shortest -c:v libx264 -c:a libfdk_aac -ar 44100 -ac 2 -pix_fmt yuv420p -color_range pc -profile:v main -y video102.mp4`
103. `ffmpeg -f lavfi -i color=c=green:s=128x72:d=1:r=1001000/1000999 -f lavfi -i sine=frequency=440:duration=1 -shortest -c:v libx264 -c:a libfdk_aac -ar 44100 -ac 2 -pix_fmt yuv420p -color_range pc -profile:v main -y video103.mp4`
104. `ffmpeg -f lavfi -i color=c=green:s=128x72:d=2:r=1000999/1001000 -f lavfi -i sine=frequency=440:duration=2 -shortest -c:v libx264 -c:a libfdk_aac -ar 44100 -ac 2 -pix_fmt yuv420p -color_range pc -profile:v main -y video104.mp4`
105. `ffmpeg -i video1.mp4 -filter:v "scale=w=1920:h=1080:force_original_aspect_ratio=disable" -c:a copy -c:v libx264 -y video105.mp4`
106. `ffmpeg -i video1.mp4 -filter:v "scale=w=4096:h=2304:force_original_aspect_ratio=disable" -c:a copy -c:v libx264 -y video106.mp4`
107. `ffmpeg -i video1.mp4 -filter:v "scale=w=8192:h=4320:force_original_aspect_ratio=disable:reset_sar=1" -c:a copy -c:v libx264 -y video107.mp4`
108. `ffmpeg -f lavfi -i color=c=green:s=8192x4320:d=0.2:r=300 -f lavfi -i sine=frequency=440:duration=0.2 -shortest -c:v libx264 -c:a libfdk_aac -ar 44100 -ac 2 -pix_fmt yuv420p -color_range pc -profile:v main -y video108.mp4`
109. `ffmpeg -i video1.mp4 -filter:v "scale=w=1920:h=2:force_original_aspect_ratio=disable,setsar=1" -c:a copy -c:v libx264 -y video109.mp4`
110. `ffmpeg -i video1.mp4 -filter:v "scale=w=2:h=1080:force_original_aspect_ratio=disable,setsar=1" -c:a copy -c:v libx264 -y video110.mp4`
111. `ffmpeg -i video1.mp4 -filter:v "scale=w=128:h=128:force_original_aspect_ratio=disable,setsar=4/3" -c:a copy -c:v libx264 -y video111.mp4`
112. `ffmpeg -i video1.mp4 -c:a copy -c:v libx264 -vf "tinterlace=interleave_top" -x264opts tff=1 -y video112.mp4`
113. `ffmpeg -i video1.mp4 -c:a copy -c:v libx264 -vf "tinterlace=interleave_bottom" -x264opts bff=1 -y video113.mp4`
114. `ffmpeg -f lavfi -i smptehdbars=size=1920x1080:rate=30:d=1 -i video1.mp4 -map 0 -map 1:a -vf "zscale=r=pc:rin=tv:transferin=bt709:primariesin=bt709:matrixin=bt709:transfer=smpte2084:primaries=bt2020:matrix=bt2020nc,format=gbrpf32le,geq=r='r(X,Y)*1.41':g='g(X,Y)*1.41':b='b(X,Y)*1.41',format=yuv420p10le" -c:v libx265 -x265-params "colorprim=bt2020:transfer=smpte2084:colormatrix=bt2020nc:master-display=G(13250,34500)B(7500,3000)R(34000,16000)WP(15635,16450)L(10000000,1)::maxcll=1000,400" -color_primaries bt2020 -color_trc smpte2084 -colorspace bt2020nc -tag:v hvc1 -shortest -y video114.mp4`
115. `ffmpeg -f lavfi -i color=c=green:s=176x144:d=1:r=30 -f lavfi -i sine=frequency=440:duration=1 -shortest -c:v libx264 -c:a libfdk_aac -ar 44100 -ac 2 -pix_fmt yuv420p -color_range pc -profile:v main -y video115.mp4`
116. `ffmpeg -f lavfi -i color=c=green:s=320x240:d=1:r=20 -f lavfi -i sine=frequency=440:duration=1 -shortest -c:v libx264 -c:a libfdk_aac -ar 44100 -ac 2 -pix_fmt yuv420p -color_range pc -profile:v main -y video116.mp4`
117. `ffmpeg -f lavfi -i color=c=green:s=320x240:d=1:r=36 -f lavfi -i sine=frequency=440:duration=1 -shortest -c:v libx264 -c:a libfdk_aac -ar 44100 -ac 2 -pix_fmt yuv420p -color_range pc -profile:v main -y video117.mp4`
118. `ffmpeg -f lavfi -i color=c=green:s=320x240:d=1:r=36 -f lavfi -i sine=frequency=440:duration=1 -shortest -c:v libx264 -c:a libfdk_aac -ar 44100 -ac 2 -pix_fmt yuv420p -color_range pc -profile:v main -b:v 500000 -y video118.mp4`
119. `ffmpeg -f lavfi -i color=c=green:s=320x480:d=1:r=30 -f lavfi -i sine=frequency=440:duration=1 -shortest -c:v libx264 -c:a libfdk_aac -ar 44100 -ac 2 -pix_fmt yuv420p -color_range pc -profile:v main -y video119.mp4`
120. `ffmpeg -f lavfi -i color=c=green:s=352x480:d=1:r=153/5 -f lavfi -i sine=frequency=440:duration=1 -shortest -c:v libx264 -c:a libfdk_aac -ar 44100 -ac 2 -pix_fmt yuv420p -color_range pc -profile:v main -y video120.mp4`
121. `ffmpeg -f lavfi -i color=c=green:s=352x480:d=1:r=60 -f lavfi -i sine=frequency=440:duration=1 -shortest -c:v libx264 -c:a libfdk_aac -ar 44100 -ac 2 -pix_fmt yuv420p -color_range pc -profile:v main -y video121.mp4`
122. `ffmpeg -f lavfi -i color=c=green:s=1280x720:d=1:r=30 -f lavfi -i sine=frequency=440:duration=1 -shortest -c:v libx264 -c:a libfdk_aac -ar 44100 -ac 2 -pix_fmt yuv420p -color_range pc -profile:v main -y video122.mp4`
123. `ffmpeg -f lavfi -i color=c=green:s=1280x720:d=1:r=60 -f lavfi -i sine=frequency=440:duration=1 -shortest -c:v libx264 -c:a libfdk_aac -ar 44100 -ac 2 -pix_fmt yuv420p -color_range pc -profile:v main -y video123.mp4`
124. `ffmpeg -f lavfi -i color=c=green:s=2048x1024:d=1:r=30 -f lavfi -i sine=frequency=440:duration=1 -shortest -c:v libx264 -c:a libfdk_aac -ar 44100 -ac 2 -pix_fmt yuv420p -color_range pc -profile:v main -y video124.mp4`
125. `ffmpeg -f lavfi -i color=c=green:s=2048x1024:d=1:r=30 -f lavfi -i sine=frequency=440:duration=1 -shortest -c:v libx264 -c:a libfdk_aac -ar 44100 -ac 2 -pix_fmt yuv420p -color_range pc -profile:v main -b:v 20000000 -y video125.mp4`
126. `ffmpeg -f lavfi -i color=c=green:s=2048x1024:d=1:r=60 -f lavfi -i sine=frequency=440:duration=1 -shortest -c:v libx264 -c:a libfdk_aac -ar 44100 -ac 2 -pix_fmt yuv420p -color_range pc -profile:v main -y video126.mp4`
127. `ffmpeg -f lavfi -i color=c=green:s=2048x1024:d=1:r=72 -f lavfi -i sine=frequency=440:duration=1 -shortest -c:v libx264 -c:a libfdk_aac -ar 44100 -ac 2 -pix_fmt yuv420p -color_range pc -profile:v main -y video127.mp4`
128. `ffmpeg -f lavfi -i color=c=green:s=1920x1080:d=1:r=120 -f lavfi -i sine=frequency=440:duration=1 -shortest -c:v libx264 -c:a libfdk_aac -ar 44100 -ac 2 -pix_fmt yuv420p -color_range pc -profile:v main -y video128.mp4`
129. `ffmpeg -f lavfi -i color=c=green:s=1920x1080:d=1:r=172 -f lavfi -i sine=frequency=440:duration=1 -shortest -c:v libx264 -c:a libfdk_aac -ar 44100 -ac 2 -pix_fmt yuv420p -color_range pc -profile:v main -y video129.mp4`
130. `ffmpeg -f lavfi -i color=c=green:s=3840x2160:d=1:r=120 -f lavfi -i sine=frequency=440:duration=1 -shortest -c:v libx264 -c:a libfdk_aac -ar 44100 -ac 2 -pix_fmt yuv420p -color_range pc -profile:v main -y video130.mp4`
131. `ffmpeg -f lavfi -i color=c=green:s=3840x2160:d=1:r=257 -f lavfi -i sine=frequency=440:duration=1 -shortest -c:v libx264 -c:a libfdk_aac -ar 44100 -ac 2 -pix_fmt yuv420p -color_range pc -profile:v main -y video131.mp4`
132. `ffmpeg -f lavfi -i color=c=green:s=3840x2160:d=1:r=300 -f lavfi -i sine=frequency=440:duration=1 -shortest -c:v libx264 -c:a libfdk_aac -ar 44100 -ac 2 -pix_fmt yuv420p -color_range pc -profile:v main -y video132.mp4`
133. `ffmpeg -i video1.mp4 -i video114.mp4 -i video111.mp4 -i video20.mp4 -i video132.mp4 -i video53.mp4 -i video54.mp4 -c copy -map 0 -map 1:v -map 6:v:1 -map 0:v -map 0:a -map 0:a -map 5:v:1 -map 2:v -map 3:s -map 3:s -map 0:v -map 0:a -map 4:v -map 0:v -map 4:v -map 3:s -map 5:v:1 -y video133.mp4`
134. `ffmpeg -i video2.mkv -attach test_image_1.png -metadata:s:t:0 mimetype=application/octet-stream -metadata:s:t:0 filename=test_image_1.png -c copy -y video134.mkv`
135. `ffmpeg -i video2.mkv -f data -i test_image_1.png -map 0 -map 1 -c copy -y video135.ts`
136. `ffmpeg -i video1.mp4 -filter:v "scale=w=2:h=16384:force_original_aspect_ratio=disable,setsar=1" -c:a copy -c:v libx264 -y video136.mp4`
137. `ffmpeg -i video1.mp4 -filter:v "scale=w=16384:h=2:force_original_aspect_ratio=disable,setsar=1" -c:a copy -c:v libx264 -y video137.mp4`
138. `ffmpeg -i video1.mp4 -filter:v "fps=fps=1,scale=w=16000:h=16000:force_original_aspect_ratio=disable,setsar=1" -c:a copy -c:v libx264 -y video138.mp4`
139. `ffmpeg -i video1.mp4 -filter:v "scale=w=16:h=4216:force_original_aspect_ratio=disable,setsar=1" -c:a copy -c:v libx265 -y video139.mp4`
140. `ffmpeg -i video1.mp4 -filter:v "scale=w=4216:h=16:force_original_aspect_ratio=disable,setsar=1" -c:a copy -c:v libx265 -y video140.mp4`
141. `ffmpeg -i video1.mp4 -filter:v "scale=w=32:h=64798:force_original_aspect_ratio=disable,setsar=1" -c:a copy -c:v libx265 -y video141.mp4`
142. `ffmpeg -i video1.mp4 -filter:v "scale=w=64798:h=32:force_original_aspect_ratio=disable,setsar=1" -c:a copy -c:v libx265 -y video142.mp4`
143. `ffmpeg -i video1.mp4 -filter:v "scale=w=64:h=65534:force_original_aspect_ratio=disable,setsar=1" -c:a copy -c:v libx265 -y video143.mp4`
144. `ffmpeg -i video1.mp4 -filter:v "scale=w=65534:h=64:force_original_aspect_ratio=disable,setsar=1" -c:a copy -c:v libx265 -y video144.mp4`
145. `ffmpeg -i video1.mp4 -filter:v "fps=fps=1,scale=w=16000:h=16000:force_original_aspect_ratio=disable,setsar=1" -c:a copy -c:v libx265 -y video145.mp4`
146. `ffmpeg -i video1.mp4 -filter:v "format=yuv444p,scale=w=65535:h=64:force_original_aspect_ratio=disable,setsar=1" -c:a copy -c:v libx265 -pix_fmt yuv444p -color_range pc -y video146.mp4`
147. `ffmpeg -i video1.mp4 -filter:v "format=yuv422p,scale=w=64:h=65535:force_original_aspect_ratio=disable,setsar=1" -c:a copy -c:v libx265 -pix_fmt yuv422p -color_range pc -y video147.mp4`
148. `ffmpeg -i video1.mp4 -filter:v "format=yuv444p,scale=w=101:h=101:force_original_aspect_ratio=disable,setsar=1" -c:a copy -c:v libx264 -pix_fmt yuv444p -color_range pc -y video148.mp4`
149. `ffmpeg -i video1.mp4 -filter:v "format=yuv422p,scale=w=100:h=101:force_original_aspect_ratio=disable,setsar=1" -c:a copy -c:v libx264 -pix_fmt yuv422p -color_range pc -y video149.mp4`
150. `ffmpeg -i video1.mp4 -ac 3 -channel_layout 3.0 -c:v copy -y video150.mp4`
151. `ffmpeg -i video1.mp4 -ac 4 -channel_layout 4.0 -c:v copy -y video151.mp4`
152. `ffmpeg -i video1.mp4 -ac 5 -channel_layout 5.0 -c:v copy -y video152.mp4`
153. `ffmpeg -i video1.mp4 -ac 7 -channel_layout "6.1(back)" -c:v copy -y video153.mp4`
154. `ffmpeg -i video1.mp4 -ac 8 -channel_layout "7.1(wide)" -c:v copy -y video154.mp4`
155. `ffmpeg -i video1.mp4 -ac 8 -channel_layout "5.1.2(back)" -c:v copy -c:a libfdk_aac -y video155.mp4`
156. `ffmpeg -i video1.mp4 -ac 7 -channel_layout "6.1(back)" -c:v copy -c:a libfdk_aac -y video156.mp4`
157. `ffmpeg -i video1.mp4 -ac 8 -channel_layout "7.1(wide)" -c:v copy -c:a libfdk_aac -y video157.mp4`
158. Manually created based off of video2.mkv
159. `ffmpeg -i video135.ts -c copy -map 0:d -y video159.ts`
160. `ffmpeg -i video1.mp4 -c copy -map 0:v -y video160.mp4`
161. `ffmpeg -i video1.mp4 -c copy -map 0:a -y video161.mp4`
162. `ffmpeg -i video1.mp4 -c copy -metadata artist="Test Artist" -metadata custom_metadata="Test Custom Metadata" -movflags +use_metadata_tags -y video162.mp4`
163. `ffmpeg -i video1.mp4 -c copy -metadata:s:v:0 language="eng" -metadata:s:a:0 language="fre" -y video163.mp4`
164. `ffmpeg -i video10.mp4 -tag:v hvc1 -c copy -y video164.mp4`
165. `ffmpeg -i video10.mp4 -c copy -y video165.mkv`
166. `ffmpeg -i video1.mp4 -filter:v "scale=w=96:h=128:force_original_aspect_ratio=disable,setsar=4/3" -c:a copy -c:v libx264 -y video166.mp4`
167. `ffmpeg -f lavfi -i color=c=green:s=128x72:d=1:r=1000999/1000998 -f lavfi -i sine=frequency=440:duration=1 -shortest -c:v libx264 -c:a libfdk_aac -ar 44100 -ac 2 -pix_fmt yuv420p -color_range pc -profile:v main -y video167.mp4`
168. `ffmpeg -i video167.mp4 -map 0:v -filter "fps=1/2" -y video168.mp4`
169. `mkvmerge -o video169.mkv video1.mp4 test_subtitles_1.idx` (.idx/.sub file made in Subtitle Edit from the .srt file)
170. `ffmpeg -i video169.mkv -map 0 -c copy -c:s dvb_subtitle -y video170.mkv`
171. `ffmpeg -f lavfi -i mandelbrot=size=1920x1080:rate=30:inner=convergence:end_scale=0.01:end_pts=30 -f lavfi -i "anoisesrc=color=white:amplitude=0.4,lowpass=f=800,tremolo=f=3:d=0.7,aecho=0.8:0.9:1200:0.3" -pix_fmt:v yuv420p -color_range pc -map 0:v -map 1:a -c:v libx264 -crf 0 -t 1 -c:a aac -strict:a -2 -b:a 288k -minrate:a 288k -maxrate:a 288k -bufsize:a 288k -y video171.mp4`
172. `ffmpeg -i video171.mp4 -c:v libx264 -crf:v 50 -c:a aac -b:a 10k -y video172.mp4`
173. `ffmpeg -i video171.mp4 -i video172.mp4 -map 0:v -map 1:a -c copy -y video173.mp4`
174. `ffmpeg -i video171.mp4 -i video172.mp4 -map 1:v -map 0:a -c copy -y video174.mp4`
175. Manually created (with a script) based of off video1.mp4
176. `ffmpeg -i video1.mp4 -filter:v "format=yuv422p,scale=w=2:h=527:force_original_aspect_ratio=disable,setsar=1" -c:a copy -c:v libx264 -pix_fmt yuv422p -color_range pc -y video176.mp4`
177. `ffmpeg -i video1.mp4 -filter:v "scale=w=16:h=4216:force_original_aspect_ratio=disable,setsar=1" -c:a copy -c:v libx264 -y video177.mp4`
178. `ffmpeg -i video1.mp4 -filter:v "format=yuv422p,scale=w=16:h=4217:force_original_aspect_ratio=disable,setsar=1" -c:a copy -c:v libx264 -pix_fmt yuv422p -color_range pc -y video178.mp4`
179. `ffmpeg -i video1.mp4 -filter:v "scale=w=16:h=4218:force_original_aspect_ratio=disable,setsar=1" -c:a copy -c:v libx264 -y video179.mp4`
180. `ffmpeg -i video1.mp4 -filter:v "format=yuv422p,scale=w=8:h=2109:force_original_aspect_ratio=disable,setsar=1" -c:a copy -c:v libx264 -pix_fmt yuv422p -color_range pc -y video180.mp4`
181. `ffmpeg -i video1.mp4 -filter:v "format=yuv422p,scale=w=32:h=64799:force_original_aspect_ratio=disable,setsar=1" -c:a copy -c:v libvpx-vp9 -pix_fmt yuv422p -color_range pc -y video181.mp4`
182. `ffmpeg -i video1.mp4 -filter:v "format=yuv444p,scale=w=63:h=64799:force_original_aspect_ratio=disable,setsar=1" -c:a copy -c:v libvpx-vp9 -pix_fmt yuv444p -color_range pc -y video182.mp4`
183. `ffmpeg -i video1.mp4 -filter:v "scale=w=32:h=64798:force_original_aspect_ratio=disable,setsar=1" -c:a copy -c:v libvpx-vp9 -y video183.mp4`
184. `ffmpeg -i video1.mp4 -filter:v "format=yuv422p,scale=w=64:h=64799:force_original_aspect_ratio=disable,setsar=1" -c:a copy -c:v libvpx-vp9 -pix_fmt yuv422p -color_range pc -y video184.mp4`
185. `ffmpeg -i video1.mp4 -filter:v "scale=w=64:h=65536:force_original_aspect_ratio=disable,setsar=1" -c:a copy -c:v libx265 -y video185.mkv`
186. `ffmpeg -i video1.mp4 -filter:v "format=yuv444p,scale=w=97:h=97:force_original_aspect_ratio=disable,setsar=1" -c:a copy -c:v libx264 -y -pix_fmt yuv444p -color_range pc video186.mp4`
187. `ffmpeg -i video1.mp4 -filter:v "format=yuv444p,scale=w=99:h=99:force_original_aspect_ratio=disable,setsar=1" -c:a copy -c:v libx264 -y -pix_fmt yuv444p -color_range pc video187.mp4`
188. `ffmpeg -f lavfi -i mandelbrot=size=1920x1080:rate=30:inner=convergence:end_scale=0.01:end_pts=30 -f lavfi -i "anoisesrc=color=white:amplitude=0.4,lowpass=f=800,tremolo=f=3:d=0.7,aecho=0.8:0.9:1200:0.3" -pix_fmt:v yuv420p -color_range pc -map 0:v -map 1:a -c:v libx264 -crf 0 -t 1 -c:a aac -strict:a -2 -b:a 288k -minrate:a 288k -maxrate:a 288k -bufsize:a 288k -timecode "00:00:05.00" -metadata artist="Test Artist" -metadata:s:v:0 language=eng -metadata:s:a:0 language=eng -y video188.mp4`
189. `ffmpeg -i video188.mp4 -c:v libx264 -crf:v 50 -c:a aac -b:a 10k -map_metadata:g 0:g -map_metadata:s:v:0 0:s:v:0 -map_metadata:s:a:0 0:s:a:0 -y video189.mp4`
190. `ffmpeg -i video188.mp4 -i video189.mp4 -map 0:v -map 1:a -c copy -map_metadata:g 0:g -map_metadata:s:v:0 0:s:v:0 -map_metadata:s:a:0 1:s:a:0 -y video190.mp4`
191. `ffmpeg -i video188.mp4 -i video189.mp4 -map 1:v -map 0:a -c copy -map_metadata:g 0:g -map_metadata:s:v:0 1:s:v:0 -map_metadata:s:a:0 0:s:a:0 -y video191.mp4`
192. `ffmpeg -i video171.mp4 -filter:v "scale=w=32:h=65536:force_original_aspect_ratio=disable,setsar=1" -c:a copy -c:v libx265 -crf 0 -y video192.mkv`
193. `ffmpeg -i video171.mp4 -i video172.mp4 -filter:v "scale=w=32:h=65536:force_original_aspect_ratio=disable,setsar=1" -c:a copy -c:v libx265 -crf 50 -map 0:v -map 1:a -y video193.mkv`
194. `ffmpeg -i video171.mp4 -i video1.mp4 -map 0:v -map 1:a -shortest -c:v libvpx -c:a copy -pix_fmt yuv420p -color_range pc -b:v 100000k -y video194.mkv`
195. `ffmpeg -i video171.mp4 -i video1.mp4 -map 0:v -map 1:a -shortest -c:v libvpx -c:a copy -pix_fmt yuv420p -color_range pc -b:v 5k -y video195.mkv`
196. Manually created based off of video2.mkv
197. `ffmpeg -i video1.mp4 -filter:v "fps=fps=1985,scale=w=16:h=4208:force_original_aspect_ratio=disable,setsar=1" -c:a copy -c:v libx264 -y video197.mp4`
198. `ffmpeg -i video1.mp4 -filter:v "fps=fps=1986,scale=w=16:h=4208:force_original_aspect_ratio=disable,setsar=1" -c:a copy -c:v libx264 -y video198.mp4`
199. `ffmpeg -i video1.mp4 -filter:v "fps=fps=992,scale=w=26:h=4208:force_original_aspect_ratio=disable,setsar=1" -c:a copy -c:v libx264 -y video199.mp4`
200. `ffmpeg -i video1.mp4 -filter:v "fps=fps=993,scale=w=26:h=4208:force_original_aspect_ratio=disable,setsar=1" -c:a copy -c:v libx264 -y video200.mp4`
