package com.axe.admin.viewmodel

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import com.axe.admin.model.AuthState
import com.axe.admin.repository.AuthRepository
import com.axe.admin.repository.LoginFailure
import com.axe.admin.repository.LoginResult
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch

enum class LoginError { MissingFields }

data class LoginUiState(
    val inProgress: Boolean = false,
    val inputError: LoginError? = null,
    val failure: LoginFailure? = null,
)

/** The password is passed in per call and is never held in state. */
class AuthViewModel(private val repository: AuthRepository) : ViewModel() {
    val authState: StateFlow<AuthState> = repository.state

    private val _login = MutableStateFlow(LoginUiState())
    val login: StateFlow<LoginUiState> = _login.asStateFlow()

    fun login(email: String, password: String) {
        if (_login.value.inProgress) return
        if (email.isBlank() || password.isEmpty()) {
            _login.value = LoginUiState(inputError = LoginError.MissingFields)
            return
        }
        _login.value = LoginUiState(inProgress = true)
        viewModelScope.launch {
            when (val result = repository.login(email, password)) {
                LoginResult.Success -> _login.value = LoginUiState()
                is LoginResult.Failure -> _login.value = LoginUiState(failure = result.reason)
            }
        }
    }

    fun dismissError() = _login.update { it.copy(inputError = null, failure = null) }

    fun logout() {
        viewModelScope.launch { repository.logout() }
    }
}
