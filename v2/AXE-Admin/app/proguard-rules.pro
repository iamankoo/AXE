# kotlinx.serialization: keep generated serializers for our @Serializable DTOs.
-keepattributes *Annotation*, InnerClasses
-keepclassmembers class com.axe.admin.** { *** Companion; }
-keepclasseswithmembers class com.axe.admin.** { kotlinx.serialization.KSerializer serializer(...); }
-dontwarn org.conscrypt.**
-dontwarn org.bouncycastle.**
-dontwarn org.openjsse.**
