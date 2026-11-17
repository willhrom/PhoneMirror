plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

android {
    namespace = "com.willhrom.phonemirror"
    compileSdk = 34

    defaultConfig {
        applicationId = "com.willhrom.phonemirror"
        minSdk = 26
        targetSdk = 34
        versionCode = (System.getenv("VERSION_CODE") ?: "1").toInt()
        versionName = System.getenv("VERSION_NAME") ?: "1.0"
    }

    // A fixed key so new versions install over old ones.
    signingConfigs {
        create("release") {
            storeFile = file("release.jks")
            storePassword = "phonemirror"
            keyAlias = "phonemirror"
            keyPassword = "phonemirror"
        }
    }

    buildTypes {
        release {
            isMinifyEnabled = false
            signingConfig = signingConfigs.getByName("release")
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    kotlinOptions {
        jvmTarget = "17"
    }
}
