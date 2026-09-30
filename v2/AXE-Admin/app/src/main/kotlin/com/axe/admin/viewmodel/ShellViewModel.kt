package com.axe.admin.viewmodel

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import com.axe.admin.repository.AdminRepository
import com.axe.admin.repository.BackendStatus
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch

/** Backend connectivity for the authenticated shell (`null` until a check completes). The shell calls [recheck] on entry. */
class ShellViewModel(private val repository: AdminRepository) : ViewModel() {
    private val _status = MutableStateFlow<BackendStatus?>(null)
    val status: StateFlow<BackendStatus?> = _status.asStateFlow()

    fun recheck() {
        _status.value = null
        viewModelScope.launch { _status.value = repository.checkBackend() }
    }
}
