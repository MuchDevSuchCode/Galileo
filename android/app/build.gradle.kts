plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

android {
    namespace = "sh.exploits.galileo"
    compileSdk = 34

    defaultConfig {
        applicationId = "sh.exploits.galileo"
        minSdk = 26
        targetSdk = 34
        versionCode = 1
        versionName = "0.1"
    }

    buildTypes {
        release {
            isMinifyEnabled = false
            proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"), "proguard-rules.pro")
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    kotlinOptions { jvmTarget = "17" }

    buildFeatures { compose = true }
    composeOptions { kotlinCompilerExtensionVersion = "1.5.14" }

    // The ESRGAN model must stay uncompressed so TFLite can memory-map it straight from the APK.
    androidResources { noCompress += "tflite" }

    packaging {
        resources {
            // BouncyCastle ships duplicate license files that clash on packaging.
            excludes += "/META-INF/{AL2.0,LGPL2.1,versions/**}"
        }
    }
}

dependencies {
    val composeBom = platform("androidx.compose:compose-bom:2024.06.00")
    implementation(composeBom)
    implementation("androidx.compose.ui:ui")
    implementation("androidx.compose.ui:ui-graphics")
    implementation("androidx.compose.material:material-icons-extended")
    implementation("androidx.compose.material3:material3")
    implementation("androidx.activity:activity-compose:1.9.0")
    implementation("androidx.lifecycle:lifecycle-runtime-ktx:2.8.3")
    implementation("androidx.lifecycle:lifecycle-viewmodel-compose:2.8.3")
    implementation("androidx.documentfile:documentfile:1.0.1")
    implementation("androidx.security:security-crypto:1.1.0-alpha06")
    implementation("androidx.biometric:biometric:1.1.0")
    implementation("androidx.fragment:fragment-ktx:1.8.1")

    // Image loading for the photo viewer / thumbnails.
    implementation("io.coil-kt:coil-compose:2.6.0")

    // Networking (relay WebSocket + HTTP) and JSON.
    implementation("com.squareup.okhttp3:okhttp:4.12.0")

    // Crypto: same BouncyCastle the desktop client uses (Ed25519 / X25519 / HKDF), so the wire format matches.
    implementation("org.bouncycastle:bcprov-jdk18on:1.78.1")

    // On-device AI super-resolution (ESRGAN) for the photo editor's Enhance/Upscale.
    implementation("org.tensorflow:tensorflow-lite:2.14.0")
}
