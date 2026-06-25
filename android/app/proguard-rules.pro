# Release build keeps minification off (see build.gradle.kts), so these are mostly belt-and-suspenders.
-keep class org.bouncycastle.** { *; }
-dontwarn org.bouncycastle.**
