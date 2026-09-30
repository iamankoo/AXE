package com.axe.admin

import android.app.Application
import com.axe.admin.data.AppContainer

class AxeAdminApp : Application() {
    lateinit var container: AppContainer
        private set

    override fun onCreate() {
        super.onCreate()
        container = AppContainer(this)
        container.authRepository.restore()
    }
}
